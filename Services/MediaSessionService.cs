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
    GlobalSystemMediaTransportControlsSession? _current;
    MediaState? _state;
    PeriodicTimer? _tick;
    long _basePosMs;
    long _baseDurMs;
    DateTimeOffset _baseTime;
    string _title = "";
    string _artist = "";
    string _album = "";
    string _app = "";
    string _playback = "Unknown";
    byte[]? _artBytes;
    string _artContentType = "image/jpeg";
    readonly object _gate = new();

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
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            Attach(_manager.GetCurrentSession());
            _tick = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            _ = Task.Run(() => TickLoop(cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "media_manager_init_failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tick?.Dispose();
        if (_current != null)
        {
            _current.MediaPropertiesChanged -= OnMediaProps;
            _current.PlaybackInfoChanged -= OnPlayback;
            _current.TimelinePropertiesChanged -= OnTimeline;
        }
        if (_manager != null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }
        return Task.CompletedTask;
    }

    async Task TickLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _tick != null && await _tick.WaitForNextTickAsync(ct))
        {
            MediaState? snapshot = null;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                var estPos = _playback == "Playing" && _baseDurMs > 0
                    ? Math.Min(_baseDurMs, _basePosMs + (long)(now - _baseTime).TotalMilliseconds)
                    : _basePosMs;
                _state = new MediaState(_playback, _app, _title, _artist, _album, Math.Max(0, estPos), Math.Max(0, _baseDurMs));
                snapshot = _state;
            }
            if (snapshot != null)
            {
                await _sse.BroadcastAsync("state", new
                {
                    playback = snapshot.Playback,
                    app = snapshot.App,
                    title = snapshot.Title,
                    artist = snapshot.Artist,
                    album = snapshot.Album,
                    position_ms = snapshot.PositionMs,
                    duration_ms = snapshot.DurationMs,
                    timestamp = DateTimeOffset.UtcNow
                }, ct);
            }
        }
    }

    void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_current != null)
        {
            _current.MediaPropertiesChanged -= OnMediaProps;
            _current.PlaybackInfoChanged -= OnPlayback;
            _current.TimelinePropertiesChanged -= OnTimeline;
        }
        _current = session;
        if (_current == null) return;
        _current.MediaPropertiesChanged += OnMediaProps;
        _current.PlaybackInfoChanged += OnPlayback;
        _current.TimelinePropertiesChanged += OnTimeline;
        var pi = _current.GetPlaybackInfo();
        var tl = _current.GetTimelineProperties();
        lock (_gate)
        {
            _playback = pi?.PlaybackStatus.ToString() ?? "Unknown";
            _app = _current.SourceAppUserModelId ?? "";
            _basePosMs = (long)(tl.Position - tl.StartTime).TotalMilliseconds;
            _baseDurMs = (long)(tl.EndTime - tl.StartTime).TotalMilliseconds;
            _baseTime = DateTimeOffset.UtcNow;
        }
        _ = RefreshMediaPropsAsync();
    }

    void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        try { Attach(sender.GetCurrentSession()); } catch { }
    }

    void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        try { Attach(sender.GetCurrentSession()); } catch { }
    }

    async void OnMediaProps(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        try { await RefreshMediaPropsAsync(); } catch { }
    }

    void OnPlayback(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        try
        {
            var pi = sender.GetPlaybackInfo();
            lock (_gate) { _playback = pi?.PlaybackStatus.ToString() ?? "Unknown"; _baseTime = DateTimeOffset.UtcNow; }
        }
        catch { }
    }

    void OnTimeline(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        try
        {
            var tl = sender.GetTimelineProperties();
            lock (_gate)
            {
                _basePosMs = (long)(tl.Position - tl.StartTime).TotalMilliseconds;
                _baseDurMs = (long)(tl.EndTime - tl.StartTime).TotalMilliseconds;
                _baseTime = DateTimeOffset.UtcNow;
            }
        }
        catch { }
    }

    async Task RefreshMediaPropsAsync()
    {
        if (_current == null) return;
        try
        {
            var props = await _current.TryGetMediaPropertiesAsync();
            var title = props?.Title ?? "";
            var artist = props?.Artist ?? "";
            var album = props?.AlbumTitle ?? "";
            var artRef = props?.Thumbnail;
            byte[]? art = null;
            string contentType = "image/jpeg";
            if (artRef != null)
            {
                var ras = await artRef.OpenReadAsync();
                if (ras != null && ras.Size > 0)
                {
                    contentType = string.IsNullOrWhiteSpace(ras.ContentType) ? "image/jpeg" : ras.ContentType;
                    using var src = ras.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await src.CopyToAsync(ms);
                    art = ms.ToArray();
                }
            }
            lock (_gate)
            {
                _title = title;
                _artist = artist;
                _album = album;
                if (art != null)
                {
                    _artBytes = art;
                    _artContentType = contentType;
                }
            }
            await _sse.BroadcastAsync("track", new
            {
                title,
                artist,
                album,
                app = _app,
                timestamp = DateTimeOffset.UtcNow
            });
        }
        catch { }
    }

    public Task<MediaState?> GetStateAsync(CancellationToken ct)
    {
        lock (_gate) { return Task.FromResult(_state); }
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

    public Task<(Stream stream, string contentType)?> GetAlbumArtAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_artBytes == null || _artBytes.Length == 0) return Task.FromResult<(Stream, string)?>(null);
            return Task.FromResult<(Stream, string)?>((new MemoryStream(_artBytes, writable: false), _artContentType));
        }
    }

    async Task<bool> TrySessionAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> act, CancellationToken ct)
    {
        try
        {
            if (_manager == null) _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var s = _manager?.GetCurrentSession();
            if (s == null) return false;
            return await act(s);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _tick?.Dispose();
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
