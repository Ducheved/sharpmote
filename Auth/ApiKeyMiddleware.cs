using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Sharpmote.App.Auth;

public class ApiKeyMiddleware
{
    readonly RequestDelegate _next;
    readonly ILogger<ApiKeyMiddleware> _logger;
    readonly IConfiguration _cfg;

    public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger, IConfiguration cfg)
    {
        _next = next;
        _logger = logger;
        _cfg = cfg;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
        if (IsPublic(path))
        {
            await _next(context);
            return;
        }

        var configuredKey = _cfg["SHARPMOTE_API_KEY"] ?? _cfg["ApiKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "about:blank",
                title = "API key not configured",
                status = 503,
                detail = "Set SHARPMOTE_API_KEY",
                instance = context.Request.Path
            }));
            return;
        }

        var headerKey = context.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(headerKey) && path == "/events")
        {
            headerKey = context.Request.Query["api_key"];
        }

        if (string.IsNullOrWhiteSpace(headerKey))
        {
            _logger.LogWarning("auth_missing_api_key");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "about:blank",
                title = "Missing API key",
                status = 401,
                detail = "X-Api-Key required",
                instance = context.Request.Path
            }));
            return;
        }

        if (!string.Equals(headerKey, configuredKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("auth_invalid_api_key");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "about:blank",
                title = "Forbidden",
                status = 403,
                detail = "Invalid API key",
                instance = context.Request.Path
            }));
            return;
        }

        await _next(context);
    }

    static bool IsPublic(string path)
    {
        if (path == "/" || path.StartsWith("/healthz") || path.StartsWith("/yandex/") || path.StartsWith("/telegram/webhook"))
            return true;
        return path.StartsWith("/css/") || path.StartsWith("/js/") || path.StartsWith("/favicon") || path.StartsWith("/assets/") || path.StartsWith("/index.html");
    }
}
