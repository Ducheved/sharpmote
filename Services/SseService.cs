using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Sharpmote.App.Services;

public class SseService
{
    readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();

    public async IAsyncEnumerable<string> Subscribe([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });
        _clients[id] = channel;
        channel.Writer.TryWrite(BuildEvent("ping", JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow })));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (await channel.Reader.WaitToReadAsync(ct))
                {
                    while (channel.Reader.TryRead(out var msg))
                        yield return msg;
                }
            }
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    public Task BroadcastAsync(string eventName, object payload, CancellationToken ct = default)
    {
        var data = JsonSerializer.Serialize(payload);
        var msg = BuildEvent(eventName, data);
        foreach (var kv in _clients)
        {
            kv.Value.Writer.TryWrite(msg);
        }
        return Task.CompletedTask;
    }

    static string BuildEvent(string name, string data)
    {
        var sb = new StringBuilder();
        sb.Append("event: ").Append(name).Append("\n");
        var lines = data.Replace("\r", "").Split('\n');
        foreach (var line in lines)
            sb.Append("data: ").Append(line).Append("\n");
        sb.Append("\n");
        return sb.ToString();
    }
}
