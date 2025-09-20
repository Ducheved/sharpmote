using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Sharpmote.App.Middleware;

public class RequestLoggingMiddleware
{
    readonly RequestDelegate _next;
    readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var requestId = Guid.NewGuid().ToString("N");
        context.Response.Headers["X-Request-Id"] = requestId;
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["trace_id"] = traceId,
            ["request_id"] = requestId,
            ["client_ip"] = GetClientIp(context)
        }))
        {
            var start = Stopwatch.GetTimestamp();
            _logger.LogInformation("{module} {action}", "http", "request_start");
            await _next(context);
            var elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            _logger.LogInformation("{module} {action} {reason} {recommendation}", "http", "request_end", context.Response.StatusCode.ToString(), $"{elapsedMs:F1}ms");
        }
    }

    static string GetClientIp(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var val))
            return val.ToString().Split(',')[0].Trim();
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}

public static class LoggingExtensions
{
    public static ILoggingBuilder AddSimpleJsonConsole(this ILoggingBuilder b)
    {
        b.AddConsole(opts =>
        {
            opts.FormatterName = "json";
        });
        b.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.JsonWriterOptions = new JsonWriterOptions
            {
                Indented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        });
        return b;
    }
}
