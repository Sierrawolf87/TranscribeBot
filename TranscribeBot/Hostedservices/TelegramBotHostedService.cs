using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TranscribeBot.Interfaces;
using TranscribeBot.Models;
using TranscribeBot.Models.Enums;
using TranscribeBot.Options;
using TranscribeBot.Services;

namespace TranscribeBot.Hostedservices;

public class TelegramBotHostedService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<TelegramBotHostedService> logger,
    IOptions<TelegramOptions> telegramOptions,
    IUserProcessingQueue userProcessingQueue)
    : IHostedService
{
    private const string SettingsCommand = "/settings";
    private const string ResetContextCommand = "/reset_context";
    private const string CompressCommand = "/compress";
    private const string ContextSummaryCommand = "/context_summary";
    private const string ContextSummaryTextCommand = "Получить саммари по всем сообщениям в контексте";
    private const string AllowUserCommand = "/allow_user";
    private const string DenyUserCommand = "/deny_user";
    private const string AllowedUsersCommand = "/allowed_users";

    private readonly TelegramOptions _telegramOptions = telegramOptions.Value;
    private TelegramBotClient? _botClient;
    private CancellationTokenSource? _stoppingCts;
    private Task? _receivingTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_telegramOptions.Token))
        {
            throw new InvalidOperationException("Telegram bot token is not configured.");
        }

        _botClient = new TelegramBotClient(_telegramOptions.Token);
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await SeedAllowedUsersAsync(cancellationToken);
        await RegisterBotCommandsAsync(_botClient, cancellationToken);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [],
            DropPendingUpdates = true
        };

        _receivingTask = _botClient.ReceiveAsync(
            updateHandler: HandleUpdateAsync,
            errorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions,
            cancellationToken: _stoppingCts.Token);

        logger.LogInformation("Telegram bot polling started.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stoppingCts is null)
        {
            return;
        }

        await _stoppingCts.CancelAsync();

        if (_receivingTask is not null)
        {
            await _receivingTask.WaitAsync(cancellationToken);
        }

        _stoppingCts.Dispose();
        _stoppingCts = null;

        logger.LogInformation("Telegram bot polling stopped.");
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            switch (update.Type)
            {
                case UpdateType.Message when update.Message is not null:
                    await HandleMessageAsync(botClient, update.Message, cancellationToken);
                    break;
                case UpdateType.CallbackQuery when update.CallbackQuery is not null:
                    await HandleCallbackQueryAsync(botClient, update.CallbackQuery, cancellationToken);
                    break;
                default:
                    logger.LogDebug("Ignored update type {UpdateType}.", update.Type);
                    break;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error while handling telegram update {UpdateId}.", update.Id);

            var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
            if (chatId is not null)
            {
                await botClient.SendMessage(
                    chatId: chatId.Value,
                    text: $"Ошибка обработки: {exception.Message}",
                    cancellationToken: cancellationToken);
            }
        }
    }

    private async Task HandleMessageAsync(
        ITelegramBotClient botClient,
        Message message,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Received message {MessageId} in chat {ChatId}.", message.Id, message.Chat.Id);

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var transcribeService = scope.ServiceProvider.GetRequiredService<ITranscribeService>();
        var telegramUserId = GetTelegramUserId(message);

        if (!await transcribeService.IsUserAllowedAsync(telegramUserId, cancellationToken))
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "У тебя нет доступа к этому боту.",
                cancellationToken: cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Text) && message.Text.StartsWith('/'))
        {
            await HandleCommandAsync(botClient, message, transcribeService, cancellationToken);
            return;
        }

        if (string.Equals(message.Text?.Trim(), ContextSummaryTextCommand, StringComparison.OrdinalIgnoreCase))
        {
            await HandleContextSummaryCommandAsync(botClient, message, transcribeService, cancellationToken);
            return;
        }

        if (message.Voice is null && message.VideoNote is null && message.Video is null)
        {
            await botClient.SendMessage(
                chatId: message.Chat.Id,
                text: "Поддерживаются голосовые сообщения, кружки и видео до 20 минут. Настройки доступны в /settings.",
                cancellationToken: cancellationToken);
            return;
        }

        var responseMessages = await userProcessingQueue.ExecuteAsync(
            telegramUserId,
            async processingCancellationToken =>
            {
                await transcribeService.GetOrCreateUserAsync(telegramUserId, message.From?.Username, processingCancellationToken);

                using var audioFile = await DownloadTelegramAudioAsync(botClient, message, processingCancellationToken);
                return await transcribeService.ProcessAudioAsync(
                    new AudioTranscriptionRequest
                    {
                        TelegramUserId = telegramUserId,
                        Username = message.From?.Username,
                        AudioStream = audioFile.Content,
                        FileName = audioFile.FileName,
                        DurationSeconds = audioFile.DurationSeconds
                    },
                    processingCancellationToken);
            },
            cancellationToken);

        foreach (var responseMessage in responseMessages)
        {
            await SendTextMessageAsync(
                botClient,
                chatId: message.Chat.Id,
                text: responseMessage,
                replyParameters: new ReplyParameters
                {
                    MessageId = message.Id,
                    AllowSendingWithoutReply = true
                },
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleCommandAsync(
        ITelegramBotClient botClient,
        Message message,
        ITranscribeService transcribeService,
        CancellationToken cancellationToken)
    {
        var command = message.Text!.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var telegramUserId = GetTelegramUserId(message);

        switch (command.ToLowerInvariant())
        {
            case "/start":
                await userProcessingQueue.ExecuteAsync(
                    telegramUserId,
                    token => transcribeService.GetOrCreateUserAsync(telegramUserId, message.From?.Username, token),
                    cancellationToken);

                await SendTextMessageAsync(
                    botClient,
                    chatId: message.Chat.Id,
                    text: BuildStartMessage(),
                    replyMarkup: await BuildSettingsMarkupAsync(transcribeService, telegramUserId, message.From?.Username, cancellationToken),
                    cancellationToken: cancellationToken);
                break;

            case SettingsCommand:
                await userProcessingQueue.ExecuteAsync(
                    telegramUserId,
                    token => transcribeService.GetOrCreateUserAsync(telegramUserId, message.From?.Username, token),
                    cancellationToken);

                await SendTextMessageAsync(
                    botClient,
                    chatId: message.Chat.Id,
                    text: await BuildSettingsTextAsync(transcribeService, telegramUserId, message.From?.Username, cancellationToken),
                    replyMarkup: await BuildSettingsMarkupAsync(transcribeService, telegramUserId, message.From?.Username, cancellationToken),
                    cancellationToken: cancellationToken);
                break;

            case ResetContextCommand:
            {
                var deleted = await userProcessingQueue.ExecuteAsync(
                    telegramUserId,
                    token => transcribeService.ResetContextAsync(telegramUserId, message.From?.Username, token),
                    cancellationToken);

                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: deleted == 0
                        ? "Контекст уже пуст."
                        : $"Контекст очищен. Удалено сообщений: {deleted}.",
                    cancellationToken: cancellationToken);
                break;
            }

            case CompressCommand:
            {
                var result = await userProcessingQueue.ExecuteAsync(
                    telegramUserId,
                    token => transcribeService.CompressContextAsync(telegramUserId, message.From?.Username, token),
                    cancellationToken);

                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: result.Message,
                    cancellationToken: cancellationToken);
                break;
            }

            case ContextSummaryCommand:
                await HandleContextSummaryCommandAsync(botClient, message, transcribeService, cancellationToken);
                break;

            case AllowUserCommand:
            {
                if (!TryParseTelegramUserIdArgument(message.Text, out var allowedTelegramUserId))
                {
                    await botClient.SendMessage(
                        chatId: message.Chat.Id,
                        text: "Использование: /allow_user <telegram_user_id>",
                        cancellationToken: cancellationToken);
                    break;
                }

                var added = await userProcessingQueue.ExecuteAsync(
                    allowedTelegramUserId,
                    token => transcribeService.AddAllowedUserAsync(allowedTelegramUserId, telegramUserId, token),
                    cancellationToken);

                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: added
                        ? $"Пользователь {allowedTelegramUserId} добавлен."
                        : $"Пользователь {allowedTelegramUserId} уже был добавлен.",
                    cancellationToken: cancellationToken);
                break;
            }

            case DenyUserCommand:
            {
                if (!TryParseTelegramUserIdArgument(message.Text, out var deniedTelegramUserId))
                {
                    await botClient.SendMessage(
                        chatId: message.Chat.Id,
                        text: "Использование: /deny_user <telegram_user_id>",
                        cancellationToken: cancellationToken);
                    break;
                }

                if (deniedTelegramUserId == telegramUserId)
                {
                    await botClient.SendMessage(
                        chatId: message.Chat.Id,
                        text: "Нельзя удалить доступ у самого себя.",
                        cancellationToken: cancellationToken);
                    break;
                }

                var removed = await userProcessingQueue.ExecuteAsync(
                    deniedTelegramUserId,
                    token => transcribeService.RemoveAllowedUserAsync(deniedTelegramUserId, token),
                    cancellationToken);

                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: removed
                        ? $"Пользователь {deniedTelegramUserId} удалён."
                        : $"Пользователь {deniedTelegramUserId} не найден.",
                    cancellationToken: cancellationToken);
                break;
            }

            case AllowedUsersCommand:
            {
                var allowedUsers = await transcribeService.GetAllowedUsersAsync(cancellationToken);
                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: allowedUsers.Count == 0
                        ? "Список разрешённых пользователей пуст."
                        : $"Разрешённые пользователи:\n{string.Join("\n", allowedUsers)}",
                    cancellationToken: cancellationToken);
                break;
            }

            default:
                await botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: "Доступные команды: /start, /settings, /reset_context, /compress, /context_summary, /allow_user, /deny_user, /allowed_users.",
                    cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task HandleCallbackQueryAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        if (callbackQuery.Message is null || string.IsNullOrWhiteSpace(callbackQuery.Data))
        {
            return;
        }

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var transcribeService = scope.ServiceProvider.GetRequiredService<ITranscribeService>();
        var telegramUserId = callbackQuery.Message.Chat.Id;
        var username = callbackQuery.From.Username;
        var callbackTelegramUserId = callbackQuery.From.Id;

        if (!await transcribeService.IsUserAllowedAsync(callbackTelegramUserId, cancellationToken))
        {
            await botClient.AnswerCallbackQuery(
                callbackQueryId: callbackQuery.Id,
                text: "У тебя нет доступа к этому боту.",
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        telegramUserId = callbackTelegramUserId;

        switch (callbackQuery.Data)
        {
            case "settings:mode:transcribe":
                if (await IsLastSelectedModeAsync(transcribeService, telegramUserId, username, ChatMode.Transcribe, cancellationToken))
                {
                    await AnswerAtLeastOneModeRequiredAsync(botClient, callbackQuery, cancellationToken);
                    return;
                }

                await transcribeService.ToggleChatModeAsync(telegramUserId, username, ChatMode.Transcribe, cancellationToken);
                break;
            case "settings:mode:normalize":
                if (await IsLastSelectedModeAsync(transcribeService, telegramUserId, username, ChatMode.Normalize, cancellationToken))
                {
                    await AnswerAtLeastOneModeRequiredAsync(botClient, callbackQuery, cancellationToken);
                    return;
                }

                await transcribeService.ToggleChatModeAsync(telegramUserId, username, ChatMode.Normalize, cancellationToken);
                break;
            case "settings:mode:summarize":
                if (await IsLastSelectedModeAsync(transcribeService, telegramUserId, username, ChatMode.Summarize, cancellationToken))
                {
                    await AnswerAtLeastOneModeRequiredAsync(botClient, callbackQuery, cancellationToken);
                    return;
                }

                await transcribeService.ToggleChatModeAsync(telegramUserId, username, ChatMode.Summarize, cancellationToken);
                break;
            case "settings:context":
                await transcribeService.ToggleUseContextAsync(telegramUserId, username, cancellationToken);
                break;
            case "settings:lang:ru":
                if (await IsCurrentLanguageAsync(transcribeService, telegramUserId, username, "ru", cancellationToken))
                {
                    await botClient.AnswerCallbackQuery(
                        callbackQueryId: callbackQuery.Id,
                        text: "Этот язык уже выбран.",
                        cancellationToken: cancellationToken);
                    return;
                }

                await transcribeService.SetLanguageAsync(telegramUserId, username, "ru", cancellationToken);
                break;
            case "settings:lang:en":
                if (await IsCurrentLanguageAsync(transcribeService, telegramUserId, username, "en", cancellationToken))
                {
                    await botClient.AnswerCallbackQuery(
                        callbackQueryId: callbackQuery.Id,
                        text: "Этот язык уже выбран.",
                        cancellationToken: cancellationToken);
                    return;
                }

                await transcribeService.SetLanguageAsync(telegramUserId, username, "en", cancellationToken);
                break;
            default:
                await botClient.AnswerCallbackQuery(
                    callbackQueryId: callbackQuery.Id,
                    text: "Неизвестное действие.",
                    cancellationToken: cancellationToken);
                return;
        }

        await EditTextMessageAsync(
            botClient,
            chatId: callbackQuery.Message.Chat.Id,
            messageId: callbackQuery.Message.Id,
            text: await BuildSettingsTextAsync(transcribeService, telegramUserId, username, cancellationToken),
            replyMarkup: await BuildSettingsMarkupAsync(transcribeService, telegramUserId, username, cancellationToken),
            cancellationToken: cancellationToken);

        await botClient.AnswerCallbackQuery(
            callbackQueryId: callbackQuery.Id,
            cancellationToken: cancellationToken);
    }

    private async Task HandleContextSummaryCommandAsync(
        ITelegramBotClient botClient,
        Message message,
        ITranscribeService transcribeService,
        CancellationToken cancellationToken)
    {
        var telegramUserId = GetTelegramUserId(message);
        var responseMessages = await userProcessingQueue.ExecuteAsync(
            telegramUserId,
            token => transcribeService.GetContextSummaryAsync(telegramUserId, message.From?.Username, token),
            cancellationToken);

        foreach (var responseMessage in responseMessages)
        {
            await SendTextMessageAsync(
                botClient,
                chatId: message.Chat.Id,
                text: responseMessage,
                replyParameters: new ReplyParameters
                {
                    MessageId = message.Id,
                    AllowSendingWithoutReply = true
                },
                cancellationToken: cancellationToken);
        }
    }

    private async Task<TelegramAudioFile> DownloadTelegramAudioAsync(
        ITelegramBotClient botClient,
        Message message,
        CancellationToken cancellationToken)
    {
        string fileId;
        string fileName;
        int durationSeconds;

        if (message.Voice is not null)
        {
            fileId = message.Voice.FileId;
            fileName = $"{message.Voice.FileUniqueId}.ogg";
            durationSeconds = message.Voice.Duration;
        }
        else if (message.VideoNote is not null)
        {
            fileId = message.VideoNote.FileId;
            fileName = $"{message.VideoNote.FileUniqueId}.mp4";
            durationSeconds = message.VideoNote.Duration;
        }
        else if (message.Video is not null)
        {
            fileId = message.Video.FileId;
            fileName = string.IsNullOrWhiteSpace(message.Video.FileName)
                ? $"{message.Video.FileUniqueId}.mp4"
                : message.Video.FileName;
            durationSeconds = message.Video.Duration;
        }
        else
        {
            throw new InvalidOperationException("Message does not contain supported media.");
        }

        var tgFile = await botClient.GetFile(fileId, cancellationToken);
        var memoryStream = new MemoryStream();
        await botClient.DownloadFile(tgFile, memoryStream, cancellationToken);
        memoryStream.Position = 0;

        return new TelegramAudioFile
        {
            Content = memoryStream,
            FileName = fileName,
            DurationSeconds = durationSeconds
        };
    }

    private static string BuildStartMessage()
    {
        return
            "Бот принимает голосовые сообщения, кружки и видео до 20 минут.\n" +
            "Команды:\n" +
            "/settings - настройки режимов, контекста и языка\n" +
            "/reset_context - очистить историю\n" +
            "/compress - принудительно сжать историю\n" +
            "/context_summary - получить подробное саммари всего контекста\n" +
            "/allow_user <id> - дать доступ пользователю\n" +
            "/deny_user <id> - забрать доступ у пользователя\n" +
            "/allowed_users - показать список пользователей с доступом";
    }

    private static async Task<bool> IsLastSelectedModeAsync(
        ITranscribeService transcribeService,
        long telegramUserId,
        string? username,
        ChatMode mode,
        CancellationToken cancellationToken)
    {
        var settings = (await transcribeService.GetOrCreateUserAsync(
            telegramUserId,
            username,
            cancellationToken)).Settings;

        return settings.ChatMode == mode;
    }

    private static Task AnswerAtLeastOneModeRequiredAsync(
        ITelegramBotClient botClient,
        CallbackQuery callbackQuery,
        CancellationToken cancellationToken)
    {
        return botClient.AnswerCallbackQuery(
            callbackQueryId: callbackQuery.Id,
            text: "Хотя бы один режим должен быть включен.",
            showAlert: true,
            cancellationToken: cancellationToken);
    }

    private static async Task<bool> IsCurrentLanguageAsync(
        ITranscribeService transcribeService,
        long telegramUserId,
        string? username,
        string language,
        CancellationToken cancellationToken)
    {
        var settings = (await transcribeService.GetOrCreateUserAsync(
            telegramUserId,
            username,
            cancellationToken)).Settings;

        return string.Equals(settings.Language, language, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> BuildSettingsTextAsync(
        ITranscribeService transcribeService,
        long telegramUserId,
        string? username,
        CancellationToken cancellationToken)
    {
        var user = await transcribeService.GetOrCreateUserAsync(telegramUserId, username, cancellationToken);
        var settings = user.Settings;

        return
            "Текущие настройки:\n" +
            $"Режимы: {FormatModes(settings.ChatMode)}\n" +
            $"Контекст: {(settings.UseContext ? "включен" : "выключен")}\n" +
            $"Язык ответа: {settings.Language ?? "ru"}";
    }

    private static async Task<InlineKeyboardMarkup> BuildSettingsMarkupAsync(
        ITranscribeService transcribeService,
        long telegramUserId,
        string? username,
        CancellationToken cancellationToken)
    {
        var user = await transcribeService.GetOrCreateUserAsync(telegramUserId, username, cancellationToken);
        var settings = user.Settings;

        return new InlineKeyboardMarkup(
        [
            [
                CreateToggleButton("Транскрипция", settings.ChatMode.HasFlag(ChatMode.Transcribe), "settings:mode:transcribe"),
                CreateToggleButton("Нормализация", settings.ChatMode.HasFlag(ChatMode.Normalize), "settings:mode:normalize")
            ],
            [
                CreateToggleButton("Саммари", settings.ChatMode.HasFlag(ChatMode.Summarize), "settings:mode:summarize")
            ],
            [
                CreateToggleButton("Контекст", settings.UseContext, "settings:context")
            ],
            [
                CreateToggleButton("RU", string.Equals(settings.Language, "ru", StringComparison.OrdinalIgnoreCase), "settings:lang:ru"),
                CreateToggleButton("EN", string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase), "settings:lang:en")
            ]
        ]);
    }

    private static InlineKeyboardButton CreateToggleButton(string label, bool isSelected, string callbackData)
    {
        var text = isSelected ? $"✅ {label}" : label;
        return InlineKeyboardButton.WithCallbackData(text, callbackData);
    }

    private static string FormatModes(ChatMode chatMode)
    {
        var modes = new List<string>();

        if (chatMode.HasFlag(ChatMode.Transcribe))
        {
            modes.Add("транскрипция");
        }

        if (chatMode.HasFlag(ChatMode.Normalize))
        {
            modes.Add("нормализация");
        }

        if (chatMode.HasFlag(ChatMode.Summarize))
        {
            modes.Add("саммари");
        }

        return modes.Count == 0 ? "не выбраны" : string.Join(", ", modes);
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        logger.LogError(exception, "Telegram bot polling error.");
        return Task.CompletedTask;
    }

    private static Task RegisterBotCommandsAsync(ITelegramBotClient botClient, CancellationToken cancellationToken)
    {
        return botClient.SetMyCommands(
            commands:
            [
                new BotCommand { Command = "start", Description = "Открыть главное меню" },
                new BotCommand { Command = "settings", Description = "Настроить режимы, контекст и язык" },
                new BotCommand { Command = "reset_context", Description = "Очистить историю контекста" },
                new BotCommand { Command = "compress", Description = "Принудительно сжать историю" },
                new BotCommand { Command = "context_summary", Description = "Получить подробное саммари контекста" },
                new BotCommand { Command = "allow_user", Description = "Дать доступ пользователю" },
                new BotCommand { Command = "deny_user", Description = "Забрать доступ у пользователя" },
                new BotCommand { Command = "allowed_users", Description = "Показать список пользователей с доступом" }
            ],
            cancellationToken: cancellationToken);
    }

    private async Task SeedAllowedUsersAsync(CancellationToken cancellationToken)
    {
        var allowedUserIds = ParseAllowedUserIds(_telegramOptions.AllowedUserIds);
        if (allowedUserIds.Count == 0)
        {
            logger.LogWarning("Telegram allowlist is empty. Configure Telegram:AllowedUserIds before starting the bot.");
            return;
        }

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var transcribeService = scope.ServiceProvider.GetRequiredService<ITranscribeService>();

        foreach (var allowedUserId in allowedUserIds)
        {
            await transcribeService.AddAllowedUserAsync(allowedUserId, allowedUserId, cancellationToken);
        }
    }

    private static long GetTelegramUserId(Message message)
    {
        return message.From?.Id ?? message.Chat.Id;
    }

    private static bool TryParseTelegramUserIdArgument(string? text, out long telegramUserId)
    {
        telegramUserId = 0;
        var parts = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts?.Length == 2 && long.TryParse(parts[1], out telegramUserId) && telegramUserId > 0;
    }

    private static IReadOnlyCollection<long> ParseAllowedUserIds(string? allowedUserIds)
    {
        if (string.IsNullOrWhiteSpace(allowedUserIds))
        {
            return [];
        }

        return allowedUserIds
            .Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => long.TryParse(id, out var parsedId) ? parsedId : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }

    private async Task SendTextMessageAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        string text,
        CancellationToken cancellationToken,
        ReplyParameters? replyParameters = null,
        ReplyMarkup? replyMarkup = null)
    {
        var formattedText = TelegramMarkdownFormatter.Render(text);

        try
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: formattedText.Text,
                entities: formattedText.Entities,
                replyParameters: replyParameters,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException exception) when (IsFormattingParsingError(exception))
        {
            logger.LogWarning(exception, "Telegram rejected formatted message. Falling back to plain text.");

            await botClient.SendMessage(
                chatId: chatId,
                text: formattedText.Text,
                replyParameters: replyParameters,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
    }

    private async Task EditTextMessageAsync(
        ITelegramBotClient botClient,
        ChatId chatId,
        int messageId,
        string text,
        CancellationToken cancellationToken,
        InlineKeyboardMarkup? replyMarkup = null)
    {
        var formattedText = TelegramMarkdownFormatter.Render(text);

        try
        {
            await botClient.EditMessageText(
                chatId: chatId,
                messageId: messageId,
                text: formattedText.Text,
                entities: formattedText.Entities,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
        catch (ApiRequestException exception) when (IsFormattingParsingError(exception))
        {
            logger.LogWarning(exception, "Telegram rejected formatted edit. Falling back to plain text.");

            await botClient.EditMessageText(
                chatId: chatId,
                messageId: messageId,
                text: formattedText.Text,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
    }

    private static bool IsFormattingParsingError(ApiRequestException exception)
    {
        return exception.Message.Contains("parse entities", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains("can't parse entities", StringComparison.OrdinalIgnoreCase);
    }
}


