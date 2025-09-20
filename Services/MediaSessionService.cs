using System.Runtime.InteropServices;
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
                if (st != null && !StateEquals(st, _lastState))
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
        var app = session.SourceAppUserModelId;
        var status = playback?.PlaybackStatus.ToString() ?? "Unknown";
        var position = (long)timeline.Position.TotalMilliseconds;
        var end = (long)timeline.EndTime.TotalMilliseconds;
        return new MediaState(status, app ?? "", title, artist, album, position, end);
    }

    public async Task PlayAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TryPlayAsync(), ct))
            return;
        SendVk(VirtualKey.VK_MEDIA_PLAY_PAUSE);
    }

    public async Task PauseAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TryPauseAsync(), ct))
            return;
        SendVk(VirtualKey.VK_MEDIA_PLAY_PAUSE);
    }

    public async Task TogglePlayPauseAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TryTogglePlayPauseAsync(), ct))
            return;
        SendVk(VirtualKey.VK_MEDIA_PLAY_PAUSE);
    }

    public async Task NextAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TrySkipNextAsync(), ct))
            return;
        SendVk(VirtualKey.VK_MEDIA_NEXT_TRACK);
    }

    public async Task PreviousAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TrySkipPreviousAsync(), ct))
            return;
        SendVk(VirtualKey.VK_MEDIA_PREV_TRACK);
    }

    public async Task StopPlaybackAsync(CancellationToken ct)
    {
        if (await TrySessionAsync(async s => await s.TryStopAsync(), ct))
            return;
        SendVk(VirtualKey.VK_MEDIA_STOP);
    }

    async Task<bool> TrySessionAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> act, CancellationToken ct)
    {
        try
        {
            if (_manager == null)
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager?.GetCurrentSession();
            if (session == null)
                return false;
            var ok = await act(session);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    static bool StateEquals(MediaState a, MediaState? b)
    {
        if (b is null) return false;
        return a.Playback == b.Playback &&
               a.Title == b.Title &&
               a.Artist == b.Artist &&
               a.Album == b.Album &&
               a.App == b.App &&
               a.PositionMs == b.PositionMs &&
               a.DurationMs == b.DurationMs;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    enum VirtualKey : ushort
    {
        VK_MEDIA_NEXT_TRACK = 0xB0,
        VK_MEDIA_PREV_TRACK = 0xB1,
        VK_MEDIA_STOP = 0xB2,
        VK_MEDIA_PLAY_PAUSE = 0xB3
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    static void SendVk(VirtualKey key)
    {
        var inputs = new INPUT[]
        {
            new INPUT
            {
                type = 1,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = (ushort)key, wScan = 0, dwFlags = 0, time = 0, dwExtraInfo = IntPtr.Zero }
                }
            },
            new INPUT
            {
                type = 1,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = (ushort)key, wScan = 0, dwFlags = 2, time = 0, dwExtraInfo = IntPtr.Zero }
                }
            }
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
