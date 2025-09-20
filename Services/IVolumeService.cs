using System.Threading;

namespace Sharpmote.App.Services;

public interface IVolumeService
{
    Task<float> GetVolumeAsync(CancellationToken ct);
    Task<bool> GetMuteAsync(CancellationToken ct);
    Task SetAsync(float level, CancellationToken ct);
    Task StepAsync(float delta, CancellationToken ct);
    Task ToggleMuteAsync(CancellationToken ct);
}
