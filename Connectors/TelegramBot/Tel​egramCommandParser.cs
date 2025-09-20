namespace Sharpmote.App.Connectors.TelegramBot;

public enum TelegramCommandKind
{
    Unknown,
    Play,
    Pause,
    Toggle,
    Next,
    Prev,
    Stop,
    VolUp,
    VolDown,
    VolSet,
    Mute,
    State
}

public record TelegramCommand(TelegramCommandKind Kind, int? VolumePercent);

public static class TelegramCommandParser
{
    public static TelegramCommand Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TelegramCommand(TelegramCommandKind.Unknown, null);
        var t = text.Trim();
        if (t.StartsWith("/play")) return new TelegramCommand(TelegramCommandKind.Play, null);
        if (t.StartsWith("/pause")) return new TelegramCommand(TelegramCommandKind.Pause, null);
        if (t.StartsWith("/toggle")) return new TelegramCommand(TelegramCommandKind.Toggle, null);
        if (t.StartsWith("/next")) return new TelegramCommand(TelegramCommandKind.Next, null);
        if (t.StartsWith("/prev")) return new TelegramCommand(TelegramCommandKind.Prev, null);
        if (t.StartsWith("/stop")) return new TelegramCommand(TelegramCommandKind.Stop, null);
        if (t.StartsWith("/volup")) return new TelegramCommand(TelegramCommandKind.VolUp, null);
        if (t.StartsWith("/voldown")) return new TelegramCommand(TelegramCommandKind.VolDown, null);
        if (t.StartsWith("/mute")) return new TelegramCommand(TelegramCommandKind.Mute, null);
        if (t.StartsWith("/state")) return new TelegramCommand(TelegramCommandKind.State, null);
        if (t.StartsWith("/vol"))
        {
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[1], out var p))
                return new TelegramCommand(TelegramCommandKind.VolSet, Math.Clamp(p, 0, 100));
            return new TelegramCommand(TelegramCommandKind.VolSet, null);
        }
        return new TelegramCommand(TelegramCommandKind.Unknown, null);
    }
}
