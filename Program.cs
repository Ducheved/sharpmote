using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Sharpmote.App.Auth;
using Sharpmote.App.Config;
using Sharpmote.App.Connectors.TelegramBot;
using Sharpmote.App.Connectors.YandexSmartHome;
using Sharpmote.App.Middleware;
using Sharpmote.App.Serialization;
using Sharpmote.App.Services;

var pre = ConfLoader.LoadFrom(AppContext.BaseDirectory, "sharpmote.conf");

var builder = WebApplication.CreateBuilder(args);

if (pre.Count > 0) builder.Configuration.AddInMemoryCollection(pre);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleJsonConsole();

var bindAddress = builder.Configuration["SHARPMOTE_BIND_ADDRESS"] ?? "0.0.0.0";
var portStr = builder.Configuration["SHARPMOTE_HTTP_PORT"] ?? "8080";
var urls = builder.Configuration["ASPNETCORE_URLS"] ?? $"http://{bindAddress}:{portStr}";
builder.WebHost.UseUrls(urls);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 1 * 1024 * 1024;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    options.Limits.MaxConcurrentConnections = 400;
});

builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = (ctx, token) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "1";
        return ValueTask.CompletedTask;
    };
    options.AddTokenBucketLimiter("api", opt =>
    {
        opt.TokenLimit = 40;
        opt.QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 80;
        opt.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
        opt.TokensPerPeriod = 40;
        opt.AutoReplenishment = true;
    });
    options.AddTokenBucketLimiter("webhook", opt =>
    {
        opt.TokenLimit = 15;
        opt.QueueLimit = 30;
        opt.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
        opt.TokensPerPeriod = 15;
        opt.AutoReplenishment = true;
    });
});
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<SseService>();
builder.Services.AddSingleton<MediaSessionService>();
builder.Services.AddSingleton<IMediaSessionService>(sp => sp.GetRequiredService<MediaSessionService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MediaSessionService>());
builder.Services.AddSingleton<VolumeService>();
builder.Services.AddSingleton<IVolumeService>(sp => sp.GetRequiredService<VolumeService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<VolumeService>());

var telegramToken = builder.Configuration["SHARPMOTE_TELEGRAM_BOT_TOKEN"];
var telegramWebhookSecret = builder.Configuration["SHARPMOTE_TELEGRAM_WEBHOOK_SECRET"];
if (!string.IsNullOrWhiteSpace(telegramToken))
{
    builder.Services.AddSingleton<TelegramHostedService>();
    if (string.IsNullOrWhiteSpace(telegramWebhookSecret))
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramHostedService>());
}

builder.Services.AddSingleton<YandexSmartHomeHandler>();

builder.Host.UseWindowsService();

var app = builder.Build();

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();

var embedded = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embedded });
app.UseStaticFiles(new StaticFileOptions { FileProvider = embedded });

app.UseMiddleware<ApiKeyMiddleware>();

app.UseRateLimiter();

app.MapGet("/healthz", () => Results.Ok(new OkDto())).RequireRateLimiting("api");

app.MapGet("/events", async (HttpContext ctx, SseService sse) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    ctx.Response.ContentType = "text/event-stream";
    var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ctx.RequestAborted);
    var token = linked.Token;
    await foreach (var msg in sse.Subscribe(token))
    {
        await ctx.Response.WriteAsync(msg, token);
        await ctx.Response.Body.FlushAsync(token);
    }
    return Results.Empty;
}).RequireRateLimiting("api");

app.MapGroup("/api/v1").MapApi();

app.MapGroup("/yandex/v1.0/user").MapYandex();

app.MapGet("/api/v1/albumart", async (HttpContext ctx, IMediaSessionService media) =>
{
    var res = await media.GetAlbumArtAsync(ctx.RequestAborted);
    if (res is null) return Results.NoContent();
    ctx.Response.Headers["Cache-Control"] = "no-store";
    return Results.Stream(res.Value.stream, res.Value.contentType);
}).RequireRateLimiting("api");

app.MapPost("/telegram/webhook/{secret}", async (HttpContext ctx, string secret, TelegramHostedService? ths, ILoggerFactory lf) =>
{
    var logger = lf.CreateLogger("telegram_webhook");
    if (ths is null)
        return Results.Problem(statusCode: 503, title: "Telegram not configured", detail: "Missing TelegramHostedService");
    var expected = app.Configuration["SHARPMOTE_TELEGRAM_WEBHOOK_SECRET"];
    if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, secret, StringComparison.Ordinal))
    {
        logger.LogWarning("invalid_webhook_secret");
        return Results.Problem(statusCode: 403, title: "Forbidden", detail: "Invalid webhook secret");
    }
    if (!string.Equals(ctx.Request.ContentType, "application/json", StringComparison.OrdinalIgnoreCase))
        return Results.Problem(statusCode: 415, title: "Unsupported Media Type");
    var body = await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync(ctx.RequestAborted);
    await ths.ProcessWebhookUpdateAsync(body, ctx.RequestAborted);
    return Results.Ok(new OkDto());
}).RequireRateLimiting("webhook");

app.Run();

public partial class Program { }

static class ApiExtensions
{
    public static RouteGroupBuilder MapApi(this RouteGroupBuilder group)
    {
        group.WithGroupName("api");
        group.MapGet("/state", async (IMediaSessionService media, IVolumeService volume, HttpContext ctx) =>
        {
            var st = await media.GetStateAsync(ctx.RequestAborted);
            if (st is null)
                return Results.Problem(statusCode: 409, title: "No active media session", detail: "No active media session", instance: ctx.Request.Path);
            var vol = await volume.GetVolumeAsync(ctx.RequestAborted);
            var mute = await volume.GetMuteAsync(ctx.RequestAborted);
            var dto = new StateDto
            {
                Playback = st.Playback,
                App = st.App,
                Title = st.Title,
                Artist = st.Artist,
                Album = st.Album,
                PositionMs = st.PositionMs,
                DurationMs = st.DurationMs,
                Timestamp = DateTimeOffset.UtcNow,
                Volume = vol,
                Mute = mute
            };
            return Results.Json(dto, AppJsonContext.JsonOptions);
        }).RequireRateLimiting("api");

        group.MapPost("/play", async (IMediaSessionService media, HttpContext ctx) =>
        {
            await media.PlayAsync(ctx.RequestAborted);
            return Results.Json(new OkDto(), AppJsonContext.JsonOptions);
        }).RequireRateLimiting("api");

        group.MapPost("/pause", async (IMediaSessionService media, HttpContext ctx) =>
        {
            await media.PauseAsync(ctx.RequestAborted);
            return Results.Json(new OkDto(), AppJsonContext.JsonOptions);
        }).RequireRateLimiting("api");

        group.MapPost("/toggle", async (IMediaSessionService media, HttpContext ctx) =>
        {
            await media.TogglePlayPauseAsync(ctx.RequestAborted);
            return Results.Json(new OkDto(), AppJsonContext.JsonOptions);
        }).RequireRateLimiting("api");

        group.MapPost("/next", async (IMediaSessionService media, HttpContext ctx) =>
        {
            await media.NextAsync(ctx.RequestAborted);
            return Results.Json(new OkDto(), AppJsonContext.JsonOptions);
        }).RequireRateLimiting("api");

        group.MapPost("/prev", async (IMediaSessionService media, HttpContext ctx) =>
        {
            await media.PreviousAsync(ctx.RequestAborted);
            return Results.Json(new OkDto(), AppJsonContext.JsonOptions);
        }).RequireRateLimiting("api");

        group.MapPost("/stop", async (IMediaSessionService media, HttpContext ctx) =>
        {
            await media.StopPlaybackAsync(ctx.RequestAborted);
            return Results.Json(new OkDto(), AppJsonContext.JsonOptions);
        }).RequireRateLimiting("api");

        group.MapPost("/volume/step", async (IVolumeService volume, HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(ctx.RequestAborted);
            using var doc = JsonDocument.Parse(json);
            var delta = doc.RootElement.TryGetProperty("delta", out var v) ? v.GetDouble() : 0d;
            await volume.StepAsync((float)delta, ctx.RequestAborted);
            return Results.Json(new OkDto(), AppJsonContext.JsonOptions);
        }).RequireRateLimiting("api");

        group.MapPost("/volume/set", async (IVolumeService volume, HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var json = await reader.ReadToEndAsync(ctx.RequestAborted);
            using var doc = JsonDocument.Parse(json);
            var level = doc.RootElement.TryGetProperty("level", out var v) ? v.GetDouble() : 0d;
            await volume.SetAsync((float)level, ctx.RequestAborted);
            return Results.Json(new OkDto(), AppJsonContext.JsonOptions);
        }).RequireRateLimiting("api");

        group.MapPost("/volume/mute", async (IVolumeService volume, HttpContext ctx) =>
        {
            await volume.ToggleMuteAsync(ctx.RequestAborted);
            return Results.Json(new OkDto(), AppJsonContext.JsonOptions);
        }).RequireRateLimiting("api");

        return group;
    }

    public static RouteGroupBuilder MapYandex(this RouteGroupBuilder group)
    {
        group.WithGroupName("yandex");
        group.MapPost("/devices", async (HttpContext ctx, YandexSmartHomeHandler handler) =>
        {
            if (!ValidateYandexAuth(ctx, handler))
                return Results.Unauthorized();
            var resp = await handler.HandleDevicesAsync(ctx.RequestAborted);
            return Results.Json(resp);
        }).RequireRateLimiting("webhook");

        group.MapPost("/devices/query", async (HttpContext ctx, YandexSmartHomeHandler handler) =>
        {
            if (!ValidateYandexAuth(ctx, handler))
                return Results.Unauthorized();
            var body = await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync(ctx.RequestAborted);
            var resp = await handler.HandleQueryAsync(body, ctx.RequestAborted);
            return Results.Json(resp);
        }).RequireRateLimiting("webhook");

        group.MapPost("/devices/action", async (HttpContext ctx, YandexSmartHomeHandler handler) =>
        {
            if (!ValidateYandexAuth(ctx, handler))
                return Results.Unauthorized();
            var body = await new StreamReader(ctx.Request.Body, Encoding.UTF8).ReadToEndAsync(ctx.RequestAborted);
            var resp = await handler.HandleActionAsync(body, ctx.RequestAborted);
            return Results.Json(resp);
        }).RequireRateLimiting("webhook");

        group.MapPost("/unlink", async (HttpContext ctx, YandexSmartHomeHandler handler) =>
        {
            if (!ValidateYandexAuth(ctx, handler))
                return Results.Unauthorized();
            var resp = await handler.HandleUnlinkAsync(ctx.RequestAborted);
            return Results.Json(resp);
        }).RequireRateLimiting("webhook");

        return group;
    }

    static bool ValidateYandexAuth(HttpContext ctx, YandexSmartHomeHandler handler)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.Ordinal))
            return false;
        var token = auth.Substring("Bearer ".Length);
        if (!string.IsNullOrWhiteSpace(handler.DevToken) && token == handler.DevToken)
            return true;
        return handler.ValidateOAuth(token, ctx);
    }
}
