using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharpmote.App.Services;

namespace Sharpmote.App.Connectors.YandexSmartHome;

public class YandexSmartHomeHandler
{
    readonly ILogger<YandexSmartHomeHandler> _logger;
    readonly IConfiguration _cfg;
    readonly IMediaSessionService _media;
    readonly IVolumeService _volume;

    public string DevToken => _cfg["SHARPMOTE_YA_OAUTH_DEV_TOKEN"] ?? "";

    public YandexSmartHomeHandler(ILogger<YandexSmartHomeHandler> logger, IConfiguration cfg, IMediaSessionService media, IVolumeService volume)
    {
        _logger = logger;
        _cfg = cfg;
        _media = media;
        _volume = volume;
    }

    public bool ValidateOAuth(string token, HttpContext ctx)
    {
        return false;
    }

    public Task<object> HandleDevicesAsync(CancellationToken ct)
    {
        var device = new
        {
            id = "sharpmote.media",
            name = "Sharpmote",
            type = "devices.types.media_device",
            capabilities = new object[]
            {
                new
                {
                    type = "devices.capabilities.on_off",
                    retrievable = true
                },
                new
                {
                    type = "devices.capabilities.range",
                    retrievable = true,
                    parameters = new
                    {
                        instance = "volume",
                        unit = "unit.percent",
                        range = new { min = 0, max = 100, precision = 5 }
                    }
                },
                new
                {
                    type = "devices.capabilities.toggle",
                    retrievable = true,
                    parameters = new { instance = "mute" }
                }
            }
        };
        var response = new
        {
            request_id = Guid.NewGuid().ToString("N"),
            payload = new
            {
                user_id = "sharpmote",
                devices = new[] { device }
            }
        };
        return Task.FromResult<object>(response);
    }

    public async Task<object> HandleQueryAsync(string body, CancellationToken ct)
    {
        var st = await _media.GetStateAsync(ct);
        var vol = await _volume.GetVolumeAsync(ct);
        var mute = await _volume.GetMuteAsync(ct);
        var deviceState = new
        {
            id = "sharpmote.media",
            capabilities = new object[]
            {
                new
                {
                    type = "devices.capabilities.on_off",
                    state = new { instance = "on", value = st != null && st.Playback == "Playing" }
                },
                new
                {
                    type = "devices.capabilities.range",
                    state = new { instance = "volume", value = (int)Math.Round(vol * 100) }
                },
                new
                {
                    type = "devices.capabilities.toggle",
                    state = new { instance = "mute", value = mute }
                }
            }
        };
        var response = new
        {
            request_id = Guid.NewGuid().ToString("N"),
            payload = new
            {
                devices = new[] { deviceState }
            }
        };
        return response;
    }

    public async Task<object> HandleActionAsync(string body, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var devices = root.GetProperty("payload").GetProperty("devices").EnumerateArray();
        var results = new List<object>();
        foreach (var dev in devices)
        {
            var id = dev.GetProperty("id").GetString() ?? "";
            var caps = dev.GetProperty("capabilities").EnumerateArray();
            var capResults = new List<object>();
            foreach (var cap in caps)
            {
                var type = cap.GetProperty("type").GetString() ?? "";
                var state = cap.GetProperty("state");
                var instance = state.GetProperty("instance").GetString() ?? "";
                var actionResult = new Dictionary<string, object?>
                {
                    ["status"] = "DONE"
                };
                try
                {
                    if (type == "devices.capabilities.on_off")
                    {
                        var value = state.GetProperty("value").GetBoolean();
                        if (value) await _media.PlayAsync(ct); else await _media.PauseAsync(ct);
                    }
                    else if (type == "devices.capabilities.range" && instance == "volume")
                    {
                        var value = state.GetProperty("value").GetInt32();
                        var level = Math.Clamp(value / 100f, 0f, 1f);
                        await _volume.SetAsync(level, ct);
                    }
                    else if (type == "devices.capabilities.toggle" && instance == "mute")
                    {
                        await _volume.ToggleMuteAsync(ct);
                    }
                    else
                    {
                        actionResult["status"] = "ERROR";
                        actionResult["error_code"] = "NOT_SUPPORTED_IN_CURRENT_MODE";
                    }
                }
                catch (Exception ex)
                {
                    actionResult["status"] = "ERROR";
                    actionResult["error_message"] = ex.Message;
                }
                capResults.Add(new
                {
                    type,
                    state = actionResult
                });
            }
            results.Add(new
            {
                id,
                capabilities = capResults
            });
        }
        var response = new
        {
            request_id = Guid.NewGuid().ToString("N"),
            payload = new
            {
                devices = results
            }
        };
        return response;
    }

    public Task<object> HandleUnlinkAsync(CancellationToken ct)
    {
        var response = new
        {
            request_id = Guid.NewGuid().ToString("N")
        };
        return Task.FromResult<object>(response);
    }
}
