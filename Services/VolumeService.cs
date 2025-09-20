using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Sharpmote.App.Services;

public class VolumeService : IVolumeService, IHostedService, IDisposable
{
    readonly ILogger<VolumeService> _logger;
    readonly SseService _sse;
    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;

    public VolumeService(ILogger<VolumeService> logger, SseService sse)
    {
        _logger = logger;
        _sse = sse;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += async n =>
            {
                await _sse.BroadcastAsync("volume", new
                {
                    volume = n.MasterVolume,
                    mute = n.Muted,
                    timestamp = DateTimeOffset.UtcNow
                });
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "volume_init_failed");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _device?.Dispose();
        _enumerator?.Dispose();
    }

    public Task<float> GetVolumeAsync(CancellationToken ct)
    {
        if (_device == null) throw new InvalidOperationException("No audio device");
        return Task.FromResult(_device.AudioEndpointVolume.MasterVolumeLevelScalar);
    }

    public Task<bool> GetMuteAsync(CancellationToken ct)
    {
        if (_device == null) throw new InvalidOperationException("No audio device");
        return Task.FromResult(_device.AudioEndpointVolume.Mute);
    }

    public Task SetAsync(float level, CancellationToken ct)
    {
        if (_device == null) throw new InvalidOperationException("No audio device");
        var clamped = Math.Clamp(level, 0f, 1f);
        _device.AudioEndpointVolume.MasterVolumeLevelScalar = clamped;
        return Task.CompletedTask;
    }

    public async Task StepAsync(float delta, CancellationToken ct)
    {
        var vol = await GetVolumeAsync(ct);
        await SetAsync(vol + (float)delta, ct);
    }

    public Task ToggleMuteAsync(CancellationToken ct)
    {
        if (_device == null) throw new InvalidOperationException("No audio device");
        _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute;
        return Task.CompletedTask;
    }
}
