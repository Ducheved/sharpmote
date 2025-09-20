using System.Threading;

namespace Sharpmote.App.Services;

public record MediaState(string Playback, string App, string Title, string Artist, string Album, long PositionMs, long DurationMs);

public interface IMediaSessionService
{
    Task<MediaState?> GetStateAsync(CancellationToken ct);
    Task PlayAsync(CancellationToken ct);
    Task PauseAsync(CancellationToken ct);
    Task TogglePlayPauseAsync(CancellationToken ct);
    Task NextAsync(CancellationToken ct);
    Task PreviousAsync(CancellationToken ct);
    Task StopPlaybackAsync(CancellationToken ct);
}
