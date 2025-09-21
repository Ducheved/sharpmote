using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Sharpmote.App.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Exceptions;

namespace Sharpmote.App.Connectors.TelegramBot;

public class TelegramHostedService : BackgroundService
{
    readonly ILogger<TelegramHostedService> _logger;
    readonly IConfiguration _cfg;
    readonly IMediaSessionService _media;
    readonly IVolumeService _volume;
    readonly ITelegramBotClient _bot;
    readonly HashSet<long> _allowed;
    readonly Dictionary<long, int> _lastMessageByChat = new();
    int _lastUpdateId;

    public TelegramHostedService(ILogger<TelegramHostedService> logger, IConfiguration cfg, IMediaSessionService media, IVolumeService volume)
    {
        _logger = logger;
        _cfg = cfg;
        _media = media;
        _volume = volume;
        var token = _cfg["SHARPMOTE_TELEGRAM_BOT_TOKEN"] ?? "";
        _bot = new TelegramBotClient(token);
        _allowed = ParseAllowed(_cfg["SHARPMOTE_TELEGRAM_ALLOWED_IDS"]);
    }

    static HashSet<long> ParseAllowed(string? csv)
    {
        var set = new HashSet<long>();
        if (string.IsNullOrWhiteSpace(csv)) return set;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (long.TryParse(part, out var id)) set.Add(id);
        return set;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
        var u = JsonSerializer.Deserialize<Update>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (u != null)
        {
            if (u.Id <= _lastUpdateId) return;
            _lastUpdateId = u.Id;
            await ProcessUpdateAsync(u, ct);
        }
    }

    async Task ProcessUpdateAsync(Update update, CancellationToken ct)
    {
        if (update.Type != UpdateType.Message && update.Type != UpdateType.CallbackQuery) return;

        if (update.Message != null)
        {
            var chatId = update.Message.Chat.Id;
            var userId = update.Message.From?.Id ?? 0;
            if (_allowed.Count > 0 && !_allowed.Contains(userId) && !_allowed.Contains(chatId))
                return;

            var text = update.Message.Text ?? "";
            await HandleCommand(text, chatId, ct);
            await UpsertMessage(chatId, ct);
        }
        else if (update.CallbackQuery != null)
        {
            var msg = update.CallbackQuery.Message;
            if (msg == null) return;
            var chatId = msg.Chat.Id;
            var userId = update.CallbackQuery.From?.Id ?? 0;
            if (_allowed.Count > 0 && !_allowed.Contains(userId) && !_allowed.Contains(chatId))
            {
                await _bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "forbidden", cancellationToken: ct);
                return;
            }
            var data = update.CallbackQuery.Data ?? "";
            if (!string.Equals(data, "/refresh", StringComparison.OrdinalIgnoreCase))
                await HandleCommand(data, chatId, ct);
            _lastMessageByChat[chatId] = msg.MessageId;
            await UpsertMessage(chatId, ct);
            await _bot.AnswerCallbackQueryAsync(update.CallbackQuery.Id, cancellationToken: ct);
        }
    }

    async Task HandleCommand(string raw, long chatId, CancellationToken ct)
    {
        if (string.Equals(raw?.Trim(), "/refresh", StringComparison.OrdinalIgnoreCase))
            return;

        var cmd = TelegramCommandParser.Parse(raw);
        if (cmd.Kind == TelegramCommandKind.Play) await _media.PlayAsync(ct);
        else if (cmd.Kind == TelegramCommandKind.Pause) await _media.PauseAsync(ct);
        else if (cmd.Kind == TelegramCommandKind.Toggle) await _media.TogglePlayPauseAsync(ct);
        else if (cmd.Kind == TelegramCommandKind.Next) await _media.NextAsync(ct);
        else if (cmd.Kind == TelegramCommandKind.Prev) await _media.PreviousAsync(ct);
        else if (cmd.Kind == TelegramCommandKind.Stop) await _media.StopPlaybackAsync(ct);
        else if (cmd.Kind == TelegramCommandKind.VolUp) await _volume.StepAsync(0.05f, ct);
        else if (cmd.Kind == TelegramCommandKind.VolDown) await _volume.StepAsync(-0.05f, ct);
        else if (cmd.Kind == TelegramCommandKind.VolSet && cmd.VolumePercent.HasValue) await _volume.SetAsync(cmd.VolumePercent.Value / 100f, ct);
        else if (cmd.Kind == TelegramCommandKind.Mute) await _volume.ToggleMuteAsync(ct);
    }

    async Task UpsertMessage(long chatId, CancellationToken ct)
    {
        var st = await _media.GetStateAsync(ct);
        var vol = await _volume.GetVolumeAsync(ct);
        var mute = await _volume.GetMuteAsync(ct);
        var line1 = $"{(st?.Playback ?? "Unknown")} • {(int)Math.Round(vol * 100)}% {(mute ? "(mute)" : "")}";
        var line2 = $"{(st?.Title ?? "-")}";
        var line3 = $"{(st?.Artist ?? "")}";
        var text = string.IsNullOrWhiteSpace(line3) ? $"{line1}\n{line2}" : $"{line1}\n{line2}\n{line3}";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new [] { InlineKeyboardButton.WithCallbackData("⏮","/prev"), InlineKeyboardButton.WithCallbackData("⏯","/toggle"), InlineKeyboardButton.WithCallbackData("⏭","/next") },
            new [] { InlineKeyboardButton.WithCallbackData("🔉","/voldown"), InlineKeyboardButton.WithCallbackData("🔇","/mute"), InlineKeyboardButton.WithCallbackData("🔊","/volup") },
            new [] { InlineKeyboardButton.WithCallbackData("🔄 Refresh","/refresh") }
        });

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
}
