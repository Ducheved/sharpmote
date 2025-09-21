using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sharpmote.App.Serialization;

public sealed class StateDto
{
    public string Playback { get; init; } = "";
    public string App { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public long PositionMs { get; init; }
    public long DurationMs { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public float Volume { get; init; }
    public bool Mute { get; init; }
}

public sealed class OkDto
{
    public bool Ok { get; init; } = true;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StateDto))]
[JsonSerializable(typeof(OkDto))]
public partial class AppJsonContext : JsonSerializerContext
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    static AppJsonContext()
    {
        JsonOptions.TypeInfoResolverChain.Add(AppJsonContext.Default);
    }
}
