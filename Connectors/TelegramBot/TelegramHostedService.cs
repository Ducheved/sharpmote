using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Sharpmote.App.Services;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Exceptions;
using System.IO;
using System.Linq;

namespace Sharpmote.App.Connectors.TelegramBot;

public class TelegramHostedService : BackgroundService
{
    readonly ILogger<TelegramHostedService> _logger;
    readonly IConfiguration _cfg;
    readonly IMediaSessionService _media;
    readonly IVolumeService _volume;
    readonly ITelegramBotClient _bot;
    readonly HashSet<long> _allowedIds;
    readonly HashSet<string> _allowedNames;
    readonly Dictionary<long, int> _lastMessageByChat = new();
    readonly Dictionary<long, AclMode> _aclPendingByChat = new();
    readonly object _aclGate = new();
    FileSystemWatcher? _confWatcher;
    readonly string _confPath;
    int _lastUpdateId;

    enum AclMode { None, Add, Remove }

    public TelegramHostedService(ILogger<TelegramHostedService> logger, IConfiguration cfg, IMediaSessionService media, IVolumeService volume)
    {
        _logger = logger;
        _cfg = cfg;
        _media = media;
        _volume = volume;
        var token = _cfg["SHARPMOTE_TELEGRAM_BOT_TOKEN"] ?? "";
        _bot = new TelegramBotClient(token);
        (_allowedIds, _allowedNames) = ParseAllowedTokens(_cfg["SHARPMOTE_TELEGRAM_ALLOWED_IDS"]);
        _confPath = Path.Combine(AppContext.BaseDirectory, "sharpmote.conf");
    }

    static (HashSet<long>, HashSet<string>) ParseAllowedTokens(string? csv)
    {
        var ids = new HashSet<long>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return (ids, names);
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = part.Trim();
            if (long.TryParse(p, out var id)) ids.Add(id);
            else
            {
                var u = NormalizeUsername(p);
                if (!string.IsNullOrEmpty(u)) names.Add(u);
            }
        }
        return (ids, names);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartConfWatcher();
        ReloadAllowedFromConfSafe();
        var webhookSecret = _cfg["SHARPMOTE_TELEGRAM_WEBHOOK_SECRET"];
        if (!string.IsNullOrWhiteSpace(webhookSecret))
            return;
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _bot.GetUpdatesAsync(offset: _lastUpdateId + 1, timeout: 20, cancellationToken: stoppingToken);
                foreach (var u in updates)
                {
                    _lastUpdateId = u.Id;
                    await ProcessUpdateAsync(u, stoppingToken);
                }
                delay = TimeSpan.FromMilliseconds(100);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "telegram_poll_error");
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
            await Task.Delay(delay, stoppingToken);
        }
    }

    public async Task ProcessWebhookUpdateAsync(string json, CancellationToken ct)
    {
        var u = JsonSerializer.Deserialize<Telegram.Bot.Types.Update>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (u != null)
        {
            if (u.Id <= _lastUpdateId) return;
            _lastUpdateId = u.Id;
            await ProcessUpdateAsync(u, ct);
        }
    }

    async Task ProcessUpdateAsync(Telegram.Bot.Types.Update update, CancellationToken ct)
    {
        if (update.Type == UpdateType.Message && update.Message != null)
        {
            var chatId = update.Message.Chat.Id;
            var userId = update.Message.From?.Id ?? 0;
            var username = update.Message.From?.Username;
            var text = update.Message.Text ?? "";
            var cmd = TelegramCommandParser.Parse(text);
            var allowed = IsAllowed(userId, chatId, username);

            if (!allowed)
            {
                if (cmd.Kind == TelegramCommandKind.Start || cmd.Kind == TelegramCommandKind.WhoAmI || cmd.Kind == TelegramCommandKind.Help)
                {
                    await HandleStartOrHelp(chatId, userId, username, cmd, ct);
                }
                return;
            }

            if (cmd.Kind == TelegramCommandKind.Start || cmd.Kind == TelegramCommandKind.Help)
            {
                await HandleStartOrHelp(chatId, userId, username, cmd, ct);
                return;
            }

            if (_aclPendingByChat.TryGetValue(chatId, out var mode))
            {
                if (cmd.Kind == TelegramCommandKind.Cancel)
                {
                    _aclPendingByChat.Remove(chatId);
                    await _bot.SendTextMessageAsync(chatId, "ок, отменил", cancellationToken: ct);
                    return;
                }
                var token = ResolveAllowedToken(text);
                if (string.IsNullOrWhiteSpace(token))
                {
                    await _bot.SendTextMessageAsync(chatId, "не могу разобрать id, пришлите @username, tg://user?id=..., https://t.me/...", cancellationToken: ct);
                    return;
                }
                if (mode == AclMode.Add)
                {
                    var added = AddAllowedToken(token);
                    if (added) PersistAllowedToConf();
                    await _bot.SendTextMessageAsync(chatId, added ? $"добавил {token}" : "уже в списке", cancellationToken: ct);
                }
                else
                {
                    var removed = RemoveAllowedToken(token);
                    if (removed) PersistAllowedToConf();
                    await _bot.SendTextMessageAsync(chatId, removed ? $"убрал {token}" : "не найдено", cancellationToken: ct);
                }
                _aclPendingByChat.Remove(chatId);
                await UpsertMessage(chatId, userId, username, ct);
                return;
            }

            var waitMode = await HandleCommand(cmd, chatId, userId, username, ct);
            if (waitMode == 2) await _media.WaitForTrackChangeAsync(TimeSpan.FromMilliseconds(2000), ct);
            else if (waitMode == 1) await _media.WaitForChangeAsync(TimeSpan.FromMilliseconds(800), ct);
            await UpsertMessage(chatId, userId, username, ct);
            if (waitMode == 2)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000);
                        await _media.ForceRefreshAsync(CancellationToken.None);
                        await UpsertMessage(chatId, userId, username, CancellationToken.None);
                    }
                    catch { }
                });
            }
            return;
        }

        if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
        {
            var msg = update.CallbackQuery.Message;
            if (msg == null) return;
            var chatId = msg.Chat.Id;
            var userId = update.CallbackQuery.From?.Id ?? 0;
            var username = update.CallbackQuery.From?.Username;
            var data = update.CallbackQuery.Data ?? "";
            var cmd = TelegramCommandParser.Parse(data);
            var allowed = IsAllowed(userId, chatId, username);

            if (cmd.Kind == TelegramCommandKind.WhoAmI)
            {
                await _bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, cancellationToken: ct);
                await _bot.SendTextMessageAsync(chatId, $"{userId}", cancellationToken: ct);
                return;
            }

            if (!allowed)
            {
                if (cmd.Kind == TelegramCommandKind.Refresh)
                {
                    await _bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, cancellationToken: ct);
                    await UpsertMessage(chatId, userId, username, ct);
                }
                else
                {
                    await _bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "нет доступа", showAlert: false, cancellationToken: ct);
                }
                return;
            }

            if (cmd.Kind == TelegramCommandKind.AllowPrompt)
            {
                _aclPendingByChat[chatId] = AclMode.Add;
                await _bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, cancellationToken: ct);
                await _bot.SendTextMessageAsync(chatId, "пришлите @username или ссылку на профиль", cancellationToken: ct);
                return;
            }

            if (cmd.Kind == TelegramCommandKind.UnallowPrompt)
            {
                _aclPendingByChat[chatId] = AclMode.Remove;
                await _bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, cancellationToken: ct);
                await _bot.SendTextMessageAsync(chatId, "кого убрать? пришлите @username или id", cancellationToken: ct);
                return;
            }

            var waitMode = await HandleCommand(cmd, chatId, userId, username, ct);
            if (waitMode == 2) await _media.WaitForTrackChangeAsync(TimeSpan.FromMilliseconds(2000), ct);
            else if (waitMode == 1) await _media.WaitForChangeAsync(TimeSpan.FromMilliseconds(800), ct);
            _lastMessageByChat[chatId] = msg.MessageId;
            await UpsertMessage(chatId, userId, username, ct);
            if (waitMode == 2)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000);
                        await _media.ForceRefreshAsync(CancellationToken.None);
                        await UpsertMessage(chatId, userId, username, CancellationToken.None);
                    }
                    catch { }
                });
            }
            await _bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, cancellationToken: ct);
        }
    }

    async Task HandleStartOrHelp(long chatId, long userId, string? username, TelegramCommand cmd, CancellationToken ct)
    {
        var allowed = IsAllowed(userId, chatId, username);
        var text = allowed
            ? "Привет! Я sharpmote. Управляю музыкой: ⏯ ⏭ ⏮, громкость, mute. Есть Refresh. Могу делиться доступом: ➕ Разрешить, ➖ Удалить.\nКоманды: /play /pause /toggle /next /prev /stop /volup /voldown /vol 0..100 /mute /state /whoami /allowed /allow /unallow /cancel"
            : $"Привет! Я sharpmote. Пока доступа нет. Твой ID: {userId}" + (string.IsNullOrWhiteSpace(username) ? "" : $" (@{username})") + ". Попроси владельца добавить тебя.";
        await _bot.SendTextMessageAsync(chatId, text, replyMarkup: BuildKeyboard(allowed), cancellationToken: ct);
    }

    async Task<int> HandleCommand(TelegramCommand cmd, long chatId, long userId, string? username, CancellationToken ct)
    {
        if (cmd.Kind == TelegramCommandKind.Refresh) return 0;
        if (cmd.Kind == TelegramCommandKind.WhoAmI) { await _bot.SendTextMessageAsync(chatId, $"{userId}", cancellationToken: ct); return 0; }
        if (cmd.Kind == TelegramCommandKind.AllowedList)
        {
            string list;
            lock (_aclGate)
            {
                var a = _allowedIds.Select(x => x.ToString()).OrderBy(x => x);
                var b = _allowedNames.Select(x => x.StartsWith("@") ? x : "@" + x).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
                list = string.Join(", ", a.Concat(b));
            }
            await _bot.SendTextMessageAsync(chatId, string.IsNullOrWhiteSpace(list) ? "(empty)" : list, cancellationToken: ct);
            return 0;
        }
        if (cmd.Kind == TelegramCommandKind.Allow && cmd.Arg != null)
        {
            var token = ResolveAllowedToken(cmd.Arg);
            if (token == null) { await _bot.SendTextMessageAsync(chatId, "не могу разобрать id", cancellationToken: ct); return 0; }
            var added = AddAllowedToken(token);
            if (added) PersistAllowedToConf();
            await _bot.SendTextMessageAsync(chatId, added ? $"добавил {token}" : "уже в списке", cancellationToken: ct);
            return 0;
        }
        if (cmd.Kind == TelegramCommandKind.Unallow && cmd.Arg != null)
        {
            var token = ResolveAllowedToken(cmd.Arg);
            if (token == null) { await _bot.SendTextMessageAsync(chatId, "не могу разобрать id", cancellationToken: ct); return 0; }
            var removed = RemoveAllowedToken(token);
            if (removed) PersistAllowedToConf();
            await _bot.SendTextMessageAsync(chatId, removed ? $"убрал {token}" : "не найдено", cancellationToken: ct);
            return 0;
        }
        if (cmd.Kind == TelegramCommandKind.AllowPrompt) { _aclPendingByChat[chatId] = AclMode.Add; await _bot.SendTextMessageAsync(chatId, "пришлите @username или ссылку на профиль", cancellationToken: ct); return 0; }
        if (cmd.Kind == TelegramCommandKind.UnallowPrompt) { _aclPendingByChat[chatId] = AclMode.Remove; await _bot.SendTextMessageAsync(chatId, "кого убрать? пришлите @username или id", cancellationToken: ct); return 0; }

        if (cmd.Kind == TelegramCommandKind.Play) { await _media.PlayAsync(ct); return 1; }
        if (cmd.Kind == TelegramCommandKind.Pause) { await _media.PauseAsync(ct); return 1; }
        if (cmd.Kind == TelegramCommandKind.Toggle) { await _media.TogglePlayPauseAsync(ct); return 1; }
        if (cmd.Kind == TelegramCommandKind.Next) { await _media.NextAsync(ct); return 2; }
        if (cmd.Kind == TelegramCommandKind.Prev) { await _media.PreviousAsync(ct); return 2; }
        if (cmd.Kind == TelegramCommandKind.Stop) { await _media.StopPlaybackAsync(ct); return 1; }
        if (cmd.Kind == TelegramCommandKind.VolUp) { await _volume.StepAsync(0.05f, ct); return 0; }
        if (cmd.Kind == TelegramCommandKind.VolDown) { await _volume.StepAsync(-0.05f, ct); return 0; }
        if (cmd.Kind == TelegramCommandKind.VolSet && cmd.VolumePercent.HasValue) { await _volume.SetAsync(cmd.VolumePercent.Value / 100f, ct); return 0; }
        if (cmd.Kind == TelegramCommandKind.Mute) { await _volume.ToggleMuteAsync(ct); return 0; }
        return 0;
    }

    InlineKeyboardMarkup BuildKeyboard(bool isAdmin)
    {
        if (isAdmin)
        {
            return new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("⏮","/prev"), InlineKeyboardButton.WithCallbackData("⏯","/toggle"), InlineKeyboardButton.WithCallbackData("⏭","/next") },
                new [] { InlineKeyboardButton.WithCallbackData("🔉","/voldown"), InlineKeyboardButton.WithCallbackData("🔇","/mute"), InlineKeyboardButton.WithCallbackData("🔊","/volup") },
                new [] { InlineKeyboardButton.WithCallbackData("👤 Кто я","/whoami"), InlineKeyboardButton.WithCallbackData("📜 Список","/allowed"), InlineKeyboardButton.WithCallbackData("🔄 Refresh","/refresh") },
                new [] { InlineKeyboardButton.WithCallbackData("➕ Разрешить","/allow_prompt"), InlineKeyboardButton.WithCallbackData("➖ Удалить","/unallow_prompt") }
            });
        }
        else
        {
            return new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("🔄 Refresh","/refresh") },
                new [] { InlineKeyboardButton.WithCallbackData("👤 Кто я","/whoami") }
            });
        }
    }

    async Task UpsertMessage(long chatId, long? userId, string? username, CancellationToken ct)
    {
        var st = await _media.GetStateAsync(ct);
        var vol = await _volume.GetVolumeAsync(ct);
        var mute = await _volume.GetMuteAsync(ct);
        var line1 = $"{(st?.Playback ?? "Unknown")} • {(int)Math.Round(vol * 100)}% {(mute ? "(mute)" : "")}";
        var line2 = $"{(st?.Title ?? "-")}";
        var line3 = $"{(st?.Artist ?? "")}";
        var text = string.IsNullOrWhiteSpace(line3) ? $"{line1}\n{line2}" : $"{line1}\n{line2}\n{line3}";
        var allowed = userId.HasValue && IsAllowed(userId.Value, chatId, username);
        var keyboard = BuildKeyboard(allowed);

        if (_lastMessageByChat.TryGetValue(chatId, out var mid))
        {
            try
            {
                await _bot.EditMessageTextAsync(chatId, mid, text, replyMarkup: keyboard, cancellationToken: ct);
                return;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified")) { return; }
            catch (ApiRequestException) { }
        }

        var msg = await _bot.SendTextMessageAsync(chatId, text, replyMarkup: keyboard, cancellationToken: ct);
        _lastMessageByChat[chatId] = msg.MessageId;
    }

    bool IsAllowed(long userId, long chatId, string? username)
    {
        lock (_aclGate)
        {
            if (_allowedIds.Contains(userId) || _allowedIds.Contains(chatId)) return true;
            if (!string.IsNullOrWhiteSpace(username))
            {
                var u = NormalizeUsername(username);
                if (!string.IsNullOrEmpty(u)) return _allowedNames.Contains(u);
            }
            return false;
        }
    }

    bool AddAllowedToken(string token)
    {
        lock (_aclGate)
        {
            if (long.TryParse(token, out var id)) return _allowedIds.Add(id);
            var u = NormalizeUsername(token);
            if (string.IsNullOrEmpty(u)) return false;
            return _allowedNames.Add(u);
        }
    }

    bool RemoveAllowedToken(string token)
    {
        lock (_aclGate)
        {
            if (long.TryParse(token, out var id)) return _allowedIds.Remove(id);
            var u = NormalizeUsername(token);
            if (string.IsNullOrEmpty(u)) return false;
            return _allowedNames.Remove(u);
        }
    }

    void StartConfWatcher()
    {
        try
        {
            _confWatcher = new FileSystemWatcher(AppContext.BaseDirectory, "sharpmote.conf");
            _confWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
            _confWatcher.Changed += (_, _) => ReloadAllowedFromConfSafe();
            _confWatcher.Created += (_, _) => ReloadAllowedFromConfSafe();
            _confWatcher.Renamed += (_, _) => ReloadAllowedFromConfSafe();
            _confWatcher.EnableRaisingEvents = true;
        }
        catch { }
    }

    void ReloadAllowedFromConfSafe()
    {
        try { ReloadAllowedFromConf(); } catch { }
    }

    void ReloadAllowedFromConf()
    {
        if (!File.Exists(_confPath)) return;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(_confPath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#") || line.StartsWith(";")) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            dict[key] = value;
        }
        if (dict.TryGetValue("SHARPMOTE_TELEGRAM_ALLOWED_IDS", out var csv))
        {
            var parsed = ParseAllowedTokens(csv);
            lock (_aclGate)
            {
                _allowedIds.Clear();
                _allowedNames.Clear();
                foreach (var id in parsed.Item1) _allowedIds.Add(id);
                foreach (var nm in parsed.Item2) _allowedNames.Add(nm);
            }
        }
    }

    void PersistAllowedToConf()
    {
        List<string> tokens;
        lock (_aclGate)
        {
            var a = _allowedIds.Select(x => x.ToString());
            var b = _allowedNames.Select(x => x.StartsWith("@") ? x : "@" + x);
            tokens = a.Concat(b).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        var csv = string.Join(",", tokens);
        SafeUpdateConfKey("SHARPMOTE_TELEGRAM_ALLOWED_IDS", csv);
        ReloadAllowedFromConfSafe();
    }

    void SafeUpdateConfKey(string key, string value)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(_confPath))
        {
            foreach (var raw in File.ReadAllLines(_confPath))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith(";")) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line[..eq].Trim();
                var v = line[(eq + 1)..].Trim();
                dict[k] = v;
            }
        }
        dict[key] = value;
        var tmp = _confPath + ".tmp";
        using (var sw = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            foreach (var kv in dict)
                sw.WriteLine($"{kv.Key}={kv.Value}");
        }
        File.Copy(tmp, _confPath, true);
        try { File.Delete(tmp); } catch { }
        Environment.SetEnvironmentVariable(key, value);
    }

    static string? ResolveAllowedToken(string input)
    {
        var s = input.Trim();
        var m = Regex.Match(s, @"tg://user\?id=(\d+)");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var id1)) return id1.ToString();
        if (long.TryParse(s, out var id2)) return id2.ToString();
        if (s.StartsWith("https://t.me/")) s = s.Substring("https://t.me/".Length);
        if (s.StartsWith("http://t.me/")) s = s.Substring("http://t.me/".Length);
        if (s.StartsWith("t.me/")) s = s.Substring("t.me/".Length);
        var q = s.IndexOf('?');
        if (q >= 0) s = s[..q];
        if (s.StartsWith("@")) s = s[1..];
        if (string.IsNullOrWhiteSpace(s)) return null;
        var u = NormalizeUsername(s);
        if (string.IsNullOrEmpty(u)) return null;
        return u;
    }

    static string NormalizeUsername(string token)
    {
        var t = token.Trim();
        if (t.StartsWith("@")) t = t[1..];
        t = t.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return t.ToLowerInvariant();
    }
}
