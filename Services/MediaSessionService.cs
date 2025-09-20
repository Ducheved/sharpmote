using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;

namespace Sharpmote.App.Services;

public class MediaSessionService : IHostedService, IMediaSessionService, IDisposable
{
    readonly ILogger<MediaSessionService> _logger;
    readonly SseService _sse;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    MediaState? _lastState;
    PeriodicTimer? _timer;
    DateTimeOffset _lastTick;
    long _estimatePosMs;
    string _lastTrackKey = "";

    public MediaSessionService(ILogger<MediaSessionService> logger, SseService sse)
    {
        _logger = logger;
        _sse = sse;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            _lastTick = DateTimeOffset.UtcNow;
            _ = Task.Run(() => PollLoop(cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "media_manager_init_failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _timer != null && await _timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var st = await ReadStateAsync(ct);
                if (st != null)
                {
                    _lastState = st;
                    await _sse.BroadcastAsync("state", new
                    {
                        playback = st.Playback,
                        app = st.App,
                        title = st.Title,
                        artist = st.Artist,
                        album = st.Album,
                        position_ms = st.PositionMs,
                        duration_ms = st.DurationMs,
                        timestamp = DateTimeOffset.UtcNow
                    }, ct);
                    await _sse.BroadcastAsync("track", new
                    {
                        title = st.Title,
                        artist = st.Artist,
                        album = st.Album,
                        app = st.App,
                        position_ms = st.PositionMs,
                        duration_ms = st.DurationMs,
                        timestamp = DateTimeOffset.UtcNow
                    }, ct);
                }
            }
            catch { }
        }
    }

    public async Task<MediaState?> GetStateAsync(CancellationToken ct)
    {
        var st = await ReadStateAsync(ct);
        return st;
    }

    async Task<MediaState?> ReadStateAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var delta = (long)(now - _lastTick).TotalMilliseconds;
        _lastTick = now;

        if (_manager == null)
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = _manager?.GetCurrentSession();
        if (session == null)
            return null;

        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var props = await session.TryGetMediaPropertiesAsync();

        var title = props?.Title ?? "";
        var artist = props?.Artist ?? "";
        var album = props?.AlbumTitle ?? "";
        var app = session.SourceAppUserModelId ?? "";
        var status = playback?.PlaybackStatus.ToString() ?? "Unknown";

        var start = timeline.StartTime;
        var rawDuration = (long)(timeline.EndTime - start).TotalMilliseconds;
        var rawPos = (long)(timeline.Position - start).TotalMilliseconds;
        if (rawDuration < 0) rawDuration = 0;
        if (rawPos < 0) rawPos = 0;
        var trackKey = $"{app}|{title}|{artist}|{album}";

        if (_lastTrackKey != trackKey)
        {
            _lastTrackKey = trackKey;
            _estimatePosMs = rawPos;
        }

        if (status == "Playing")
        {
            if (rawDuration > 0)
                _estimatePosMs = rawPos > 0 ? rawPos : Math.Min(rawDuration, _estimatePosMs + Math.Max(0, delta));
            else
                _estimatePosMs = Math.Max(0, _estimatePosMs + Math.Max(0, delta));
        }
        else
        {
            if (rawPos > 0)
                _estimatePosMs = rawPos;
        }

        if (rawDuration > 0 && _estimatePosMs > rawDuration)
            _estimatePosMs = rawDuration;

        return new MediaState(status, app, title, artist, album, _estimatePosMs, rawDuration);
    }

    public async Task PlayAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TryPlayAsync(), ct)) return;
        SendVk(VirtualKey.VK_MEDIA_PLAY_PAUSE);
    }

    public async Task PauseAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TryPauseAsync(), ct)) return;
        SendVk(VirtualKey.VK_MEDIA_PLAY_PAUSE);
    }

    public async Task TogglePlayPauseAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TryTogglePlayPauseAsync(), ct)) return;
        SendVk(VirtualKey.VK_MEDIA_PLAY_PAUSE);
    }

    public async Task NextAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TrySkipNextAsync(), ct)) return;
        SendVk(VirtualKey.VK_MEDIA_NEXT_TRACK);
    }

    public async Task PreviousAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TrySkipPreviousAsync(), ct)) return;
        SendVk(VirtualKey.VK_MEDIA_PREV_TRACK);
    }

    public async Task StopPlaybackAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TryStopAsync(), ct)) return;
        SendVk(VirtualKey.VK_MEDIA_STOP);
    }

    public async Task<(Stream stream, string contentType)?> GetAlbumArtAsync(CancellationToken ct)
    {
        try
        {
            if (_manager == null)
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager?.GetCurrentSession();
            if (session == null) return null;
            var props = await session.TryGetMediaPropertiesAsync();
            var thumb = props?.Thumbnail;
            if (thumb == null) return null;
            var ras = await thumb.OpenReadAsync();
            if (ras == null || ras.Size == 0) return null;
            var contentType = string.IsNullOrWhiteSpace(ras.ContentType) ? "image/jpeg" : ras.ContentType;
            using var src = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await src.CopyToAsync(ms, ct);
            ms.Position = 0;
            return (ms, contentType);
        }
        catch
        {
            return null;
        }
    }

    async Task<bool> TrySessionAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> act, CancellationToken ct)
    {
        try
        {
            if (_manager == null)
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager?.GetCurrentSession();
            if (session == null) return false;
            var ok = await act(session);
            return ok;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    enum VirtualKey : ushort { VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_STOP = 0xB2, VK_MEDIA_PLAY_PAUSE = 0xB3 }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    static void SendVk(VirtualKey key)
    {
        var inputs = new INPUT[]
        {
            new INPUT{ type=1, U=new InputUnion{ ki=new KEYBDINPUT{ wVk=(ushort)key, dwFlags=0 } } },
            new INPUT{ type=1, U=new InputUnion{ ki=new KEYBDINPUT{ wVk=(ushort)key, dwFlags=2 } } }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
