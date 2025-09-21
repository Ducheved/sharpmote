namespace Sharpmote.App.Connectors.TelegramBot;

public enum TelegramCommandKind
{
    Unknown,
    Start,
    Help,
    Cancel,
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
    State,
    Allow,
    Unallow,
    AllowedList,
    WhoAmI,
    AllowPrompt,
    UnallowPrompt,
    Refresh
}

public record TelegramCommand(TelegramCommandKind Kind, int? VolumePercent, string? Arg);

public static class TelegramCommandParser
{
    public static TelegramCommand Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TelegramCommand(TelegramCommandKind.Unknown, null, null);
        var t = text.Trim();
        if (t.StartsWith("/start")) return new TelegramCommand(TelegramCommandKind.Start, null, t.Length > 7 ? t[7..].Trim() : null);
        if (t.StartsWith("/help")) return new TelegramCommand(TelegramCommandKind.Help, null, null);
        if (t.StartsWith("/cancel")) return new TelegramCommand(TelegramCommandKind.Cancel, null, null);
        if (t.StartsWith("/play")) return new TelegramCommand(TelegramCommandKind.Play, null, null);
        if (t.StartsWith("/pause")) return new TelegramCommand(TelegramCommandKind.Pause, null, null);
        if (t.StartsWith("/toggle")) return new TelegramCommand(TelegramCommandKind.Toggle, null, null);
        if (t.StartsWith("/next")) return new TelegramCommand(TelegramCommandKind.Next, null, null);
        if (t.StartsWith("/prev")) return new TelegramCommand(TelegramCommandKind.Prev, null, null);
        if (t.StartsWith("/stop")) return new TelegramCommand(TelegramCommandKind.Stop, null, null);
        if (t.StartsWith("/volup")) return new TelegramCommand(TelegramCommandKind.VolUp, null, null);
        if (t.StartsWith("/voldown")) return new TelegramCommand(TelegramCommandKind.VolDown, null, null);
        if (t.StartsWith("/mute")) return new TelegramCommand(TelegramCommandKind.Mute, null, null);
        if (t.StartsWith("/state")) return new TelegramCommand(TelegramCommandKind.State, null, null);
        if (t.StartsWith("/whoami")) return new TelegramCommand(TelegramCommandKind.WhoAmI, null, null);
        if (t.StartsWith("/allowed")) return new TelegramCommand(TelegramCommandKind.AllowedList, null, null);
        if (t.StartsWith("/allow_prompt")) return new TelegramCommand(TelegramCommandKind.AllowPrompt, null, null);
        if (t.StartsWith("/unallow_prompt")) return new TelegramCommand(TelegramCommandKind.UnallowPrompt, null, null);
        if (t.StartsWith("/allow"))
        {
            var arg = t.Length > 6 ? t[6..].Trim() : null;
            return new TelegramCommand(TelegramCommandKind.Allow, null, arg);
        }
        if (t.StartsWith("/unallow") || t.StartsWith("/remove"))
        {
            var arg = t.Contains(' ') ? t[(t.IndexOf(' ') + 1)..].Trim() : null;
            return new TelegramCommand(TelegramCommandKind.Unallow, null, arg);
        }
        if (t.StartsWith("/refresh")) return new TelegramCommand(TelegramCommandKind.Refresh, null, null);
        if (t.StartsWith("/vol"))
        {
            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[1], out var p))
                return new TelegramCommand(TelegramCommandKind.VolSet, Math.Clamp(p, 0, 100), null);
            return new TelegramCommand(TelegramCommandKind.VolSet, null, null);
        }
        return new TelegramCommand(TelegramCommandKind.Unknown, null, null);
    }
}
