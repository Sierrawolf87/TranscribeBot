using FFMpegCore;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using TranscribeBot.Data;
using TranscribeBot.Interfaces;
using TranscribeBot.Models;
using TranscribeBot.Models.Ai;
using TranscribeBot.Models.Enums;

namespace TranscribeBot.Services;

public class TranscribeService(
    ApplicationDbContext dbContext,
    IAiService aiService,
    ILogger<TranscribeService> logger)
    : ITranscribeService
{
    private const int MaxDurationSeconds = 20 * 60;
    private const int CompressionThresholdOutputTokens = 30000;
    private const int TelegramMessageLimit = 4000;

    public async Task<User> GetOrCreateUserAsync(
        long telegramUserId,
        string? username,
        CancellationToken cancellationToken = default)
    {
        var user = await GetOrCreateTrackedUserAsync(telegramUserId, username, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<UserSettings> ToggleChatModeAsync(
        long telegramUserId,
        string? username,
        ChatMode mode,
        CancellationToken cancellationToken = default)
    {
        var user = await GetOrCreateTrackedUserAsync(telegramUserId, username, cancellationToken);
        var newMode = user.Settings.ChatMode ^ mode;

        if (newMode == 0)
        {
            return user.Settings;
        }

        user.Settings.ChatMode = newMode;
        await dbContext.SaveChangesAsync(cancellationToken);
        return user.Settings;
    }

    public async Task<UserSettings> ToggleUseContextAsync(
        long telegramUserId,
        string? username,
        CancellationToken cancellationToken = default)
    {
        var user = await GetOrCreateTrackedUserAsync(telegramUserId, username, cancellationToken);
        user.Settings.UseContext = !user.Settings.UseContext;
        await dbContext.SaveChangesAsync(cancellationToken);
        return user.Settings;
    }

    public async Task<UserSettings> SetLanguageAsync(
        long telegramUserId,
        string? username,
        string language,
        CancellationToken cancellationToken = default)
    {
        var user = await GetOrCreateTrackedUserAsync(telegramUserId, username, cancellationToken);
        user.Settings.Language = NormalizeLanguage(language);
        await dbContext.SaveChangesAsync(cancellationToken);
        return user.Settings;
    }

    public async Task<int> ResetContextAsync(
        long telegramUserId,
        string? username,
        CancellationToken cancellationToken = default)
    {
        var user = await GetOrCreateTrackedUserAsync(telegramUserId, username, cancellationToken);
        var messages = await dbContext.Messages
            .Where(message => message.UserId == user.Id)
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
        {
            return 0;
        }

        dbContext.Messages.RemoveRange(messages);
        await dbContext.SaveChangesAsync(cancellationToken);
        return messages.Count;
    }

    public async Task<CompressionResult> CompressContextAsync(
        long telegramUserId,
        string? username,
        CancellationToken cancellationToken = default)
    {
        var user = await GetOrCreateTrackedUserAsync(telegramUserId, username, cancellationToken);
        var messages = await dbContext.Messages
            .Where(message => message.UserId == user.Id)
            .OrderBy(message => message.CreatedAt)
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
        {
            return new CompressionResult
            {
                Message = "Контекст пуст. Сжимать нечего."
            };
        }

        var contextMessages = BuildContextMessages(messages);
        var aiResult = await aiService.CompressContextAsync(
            new CompressContextAiRequest
            {
                ResponseLanguage = user.Settings.Language ?? "ru",
                ContextMessages = contextMessages
            },
            cancellationToken);

        if (string.IsNullOrWhiteSpace(aiResult.CompressedText))
        {
            throw new InvalidOperationException("AI service returned an empty compressed context.");
        }

        var mergedTranscription = BuildMergedTranscription(messages);

        dbContext.Messages.RemoveRange(messages);
        dbContext.Messages.Add(new Messages
        {
            UserId = user.Id,
            Transcription = mergedTranscription,
            CompressedText = aiResult.CompressedText,
            InputTokens = aiResult.InputTokens,
            OutputTokens = aiResult.OutputTokens
        });

        user.TotalTokenUsage += aiResult.InputTokens + aiResult.OutputTokens;

        await dbContext.SaveChangesAsync(cancellationToken);

        return new CompressionResult
        {
            WasCompressed = true,
            RemovedMessagesCount = messages.Count,
            InputTokens = aiResult.InputTokens,
            OutputTokens = aiResult.OutputTokens,
            Message = $"Контекст сжат. Сообщений объединено: {messages.Count}."
        };
    }

    public async Task<IReadOnlyCollection<string>> GetContextSummaryAsync(
        long telegramUserId,
        string? username,
        CancellationToken cancellationToken = default)
    {
        var user = await GetOrCreateTrackedUserAsync(telegramUserId, username, cancellationToken);
        var messages = await dbContext.Messages
            .Where(message => message.UserId == user.Id)
            .OrderBy(message => message.CreatedAt)
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
        {
            return ["Контекст пуст. Саммари строить не из чего."];
        }

        var aiResult = await aiService.SummarizeContextAsync(
            new ContextSummaryAiRequest
            {
                ResponseLanguage = user.Settings.Language ?? "ru",
                Messages = messages
                    .Select(message => new ContextSummarySourceMessage
                    {
                        CreatedAt = message.CreatedAt,
                        Transcription = message.Transcription,
                        CompressedText = message.CompressedText
                    })
                    .ToList()
            },
            cancellationToken);

        if (string.IsNullOrWhiteSpace(aiResult.Summary))
        {
            throw new InvalidOperationException("AI service returned an empty context summary.");
        }

        user.TotalTokenUsage += aiResult.InputTokens + aiResult.OutputTokens;
        await dbContext.SaveChangesAsync(cancellationToken);

        return SplitForTelegram([$"Саммари по всему контексту\n{aiResult.Summary}"]);
    }

    public async Task<IReadOnlyCollection<string>> ProcessAudioAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.DurationSeconds > MaxDurationSeconds)
        {
            throw new InvalidOperationException("Файл длиннее 20 минут. Такой файл не обрабатывается.");
        }

        var user = await GetOrCreateTrackedUserAsync(request.TelegramUserId, request.Username, cancellationToken);

        if (user.Settings.UseContext)
        {
            var outputTokens = await dbContext.Messages
                .Where(message => message.UserId == user.Id)
                .SumAsync(message => message.OutputTokens, cancellationToken);

            if (outputTokens > CompressionThresholdOutputTokens)
            {
                await CompressContextAsync(request.TelegramUserId, request.Username, cancellationToken);
                user = await GetOrCreateTrackedUserAsync(request.TelegramUserId, request.Username, cancellationToken);
            }
        }

        var inputPath = CreateTempPath(request.FileName);
        var preparedAudioPath = CreateTempPath("prepared.mp3");

        try
        {
            await SaveToFileAsync(request.AudioStream, inputPath, cancellationToken);
            await EnsureAudioPreparationAsync(inputPath, preparedAudioPath, cancellationToken);

            await using var preparedAudioStream = File.OpenRead(preparedAudioPath);

            var contextMessages = user.Settings.UseContext
                ? await LoadContextMessagesAsync(user.Id, cancellationToken)
                : [];

            var aiChatMode = user.Settings.UseContext
                ? user.Settings.ChatMode | ChatMode.Transcribe
                : user.Settings.ChatMode;

            var aiRequest = new TranscriptionAiRequest
            {
                AudioStream = preparedAudioStream,
                FileName = Path.GetFileName(preparedAudioPath),
                ResponseLanguage = user.Settings.Language ?? "ru",
                ChatMode = aiChatMode,
                ContextMessages = contextMessages
            };

            var aiResult = await aiService.ProcessAudioAsync(aiRequest, cancellationToken);
            var totalInputTokens = aiResult.InputTokens;
            var totalOutputTokens = aiResult.OutputTokens;

            if (LooksLikeCorruptedModelOutput(aiResult))
            {
                logger.LogWarning(
                    "Suspicious AI output detected for TelegramUserId={TelegramUserId}. Retrying without context.",
                    request.TelegramUserId);

                preparedAudioStream.Position = 0;
                aiRequest.ContextMessages = [];
                aiResult = await aiService.ProcessAudioAsync(aiRequest, cancellationToken);
                totalInputTokens += aiResult.InputTokens;
                totalOutputTokens += aiResult.OutputTokens;
            }

            if (LooksLikeCorruptedModelOutput(aiResult))
            {
                throw new InvalidOperationException(
                    "Модель вернула некорректный результат. Попробуй отправить сообщение ещё раз.");
            }

            user.TotalTokenUsage += totalInputTokens + totalOutputTokens;

            if (user.Settings.UseContext)
            {
                dbContext.Messages.Add(new Messages
                {
                    UserId = user.Id,
                    Transcription = aiResult.Transcription,
                    Normalized = aiResult.Normalized,
                    Summarized = aiResult.Summarized,
                    InputTokens = totalInputTokens,
                    OutputTokens = totalOutputTokens
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            return BuildTelegramMessages(aiResult, user.Settings.ChatMode);
        }
        finally
        {
            DeleteIfExists(inputPath);
            DeleteIfExists(preparedAudioPath);
        }
    }

    public async Task<bool> IsUserAllowedAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.AllowedUsers
            .AnyAsync(user => user.TelegramId == telegramUserId, cancellationToken);
    }

    public async Task<bool> AddAllowedUserAsync(
        long telegramUserId,
        long addedByTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        if (await dbContext.AllowedUsers.AnyAsync(user => user.TelegramId == telegramUserId, cancellationToken))
        {
            return false;
        }

        dbContext.AllowedUsers.Add(new AllowedUser
        {
            TelegramId = telegramUserId,
            AddedByTelegramId = addedByTelegramUserId
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveAllowedUserAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default)
    {
        var allowedUser = await dbContext.AllowedUsers
            .FirstOrDefaultAsync(user => user.TelegramId == telegramUserId, cancellationToken);

        if (allowedUser is null)
        {
            return false;
        }

        dbContext.AllowedUsers.Remove(allowedUser);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyCollection<long>> GetAllowedUsersAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.AllowedUsers
            .OrderBy(user => user.TelegramId)
            .Select(user => user.TelegramId)
            .ToListAsync(cancellationToken);
    }

    private async Task<User> GetOrCreateTrackedUserAsync(
        long telegramUserId,
        string? username,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .Include(item => item.Settings)
            .FirstOrDefaultAsync(item => item.TelegramId == telegramUserId, cancellationToken);

        if (user is null)
        {
            user = new User
            {
                TelegramId = telegramUserId,
                Username = username,
                Settings = new UserSettings()
            };

            dbContext.Users.Add(user);
            return user;
        }

        if (!string.Equals(user.Username, username, StringComparison.Ordinal))
        {
            user.Username = username;
        }

        if (user.Settings is null)
        {
            user.Settings = new UserSettings();
        }

        return user;
    }

    private async Task<List<AiRequestContextMessage>> LoadContextMessagesAsync(int userId, CancellationToken cancellationToken)
    {
        var messages = await dbContext.Messages
            .Where(message => message.UserId == userId)
            .OrderBy(message => message.CreatedAt)
            .ToListAsync(cancellationToken);

        return BuildContextMessages(messages);
    }

    private static List<AiRequestContextMessage> BuildContextMessages(IEnumerable<Messages> messages)
    {
        return messages
            .Select(message => new AiRequestContextMessage
            {
                CreatedAt = message.CreatedAt,
                Content = SelectContextContent(message)
            })
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .ToList();
    }

    private static string SelectContextContent(Messages message)
    {
        return message.CompressedText
               ?? message.Summarized
               ?? message.Normalized
               ?? message.Transcription
               ?? string.Empty;
    }

    private static string? BuildMergedTranscription(IEnumerable<Messages> messages)
    {
        var parts = messages
            .Where(message => !string.IsNullOrWhiteSpace(message.Transcription))
            .Select(message => $"[{message.CreatedAt:u}]\n{message.Transcription!.Trim()}")
            .ToList();

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static async Task SaveToFileAsync(Stream source, string path, CancellationToken cancellationToken)
    {
        source.Position = 0;
        await using var target = File.Create(path);
        await source.CopyToAsync(target, cancellationToken);
    }

    private async Task EnsureAudioPreparationAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        IMediaAnalysis mediaInfo;
        try
        {
            mediaInfo = await FFProbe.AnalyseAsync(inputPath, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (IsMissingFfmpegTool(exception, "ffprobe"))
        {
            throw new InvalidOperationException(
                "Не найден ffprobe. Установи ffmpeg/ffprobe или запускай сервис в обновленном Docker-образе.",
                exception);
        }

        var duration = mediaInfo.Duration;

        if (duration > TimeSpan.FromSeconds(MaxDurationSeconds))
        {
            throw new InvalidOperationException("Файл длиннее 20 минут. Такой файл не обрабатывается.");
        }

        logger.LogInformation(
            "Preparing audio for AI processing. Input={InputPath}, Duration={Duration}",
            inputPath,
            duration);

        try
        {
            FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(outputPath, true, options => options
                    .ForceFormat("mp3")
                    .WithCustomArgument("-vn -ac 1 -ar 16000"))
                .ProcessSynchronously();
        }
        catch (Exception exception) when (IsMissingFfmpegTool(exception, "ffmpeg"))
        {
            throw new InvalidOperationException(
                "Не найден ffmpeg. Установи ffmpeg/ffprobe или запускай сервис в обновленном Docker-образе.",
                exception);
        }
    }

    private static IReadOnlyCollection<string> BuildTelegramMessages(AudioProcessingResult aiResult, ChatMode chatMode)
    {
        var blocks = new List<string>();

        if (chatMode.HasFlag(ChatMode.Transcribe) && !string.IsNullOrWhiteSpace(aiResult.Transcription))
        {
            blocks.Add($"Транскрипция\n{aiResult.Transcription}");
        }

        if (chatMode.HasFlag(ChatMode.Normalize) && !string.IsNullOrWhiteSpace(aiResult.Normalized))
        {
            blocks.Add($"Нормализация\n{aiResult.Normalized}");
        }

        if (chatMode.HasFlag(ChatMode.Summarize) && !string.IsNullOrWhiteSpace(aiResult.Summarized))
        {
            blocks.Add($"Саммари\n{aiResult.Summarized}");
        }

        if (blocks.Count == 0)
        {
            blocks.Add("Модель не вернула текст для выбранных режимов.");
        }

        return SplitForTelegram(blocks);
    }

    private static IReadOnlyCollection<string> SplitForTelegram(IReadOnlyCollection<string> blocks)
    {
        var result = new List<string>();
        var current = string.Empty;

        foreach (var block in blocks)
        {
            if (block.Length > TelegramMessageLimit)
            {
                if (!string.IsNullOrWhiteSpace(current))
                {
                    result.Add(current.Trim());
                    current = string.Empty;
                }

                result.AddRange(SplitLargeBlock(block));
                continue;
            }

            var candidate = string.IsNullOrWhiteSpace(current)
                ? block
                : $"{current}\n\n{block}";

            if (candidate.Length > TelegramMessageLimit)
            {
                result.Add(current.Trim());
                current = block;
                continue;
            }

            current = candidate;
        }

        if (!string.IsNullOrWhiteSpace(current))
        {
            result.Add(current.Trim());
        }

        return result;
    }

    private static IEnumerable<string> SplitLargeBlock(string text)
    {
        var offset = 0;

        while (offset < text.Length)
        {
            var length = Math.Min(TelegramMessageLimit, text.Length - offset);
            yield return text.Substring(offset, length).Trim();
            offset += length;
        }
    }

    private static string NormalizeLanguage(string language)
    {
        return string.IsNullOrWhiteSpace(language)
            ? "ru"
            : language.Trim().ToLowerInvariant();
    }

    private static string CreateTempPath(string fileName)
    {
        var safeExtension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(safeExtension))
        {
            safeExtension = ".tmp";
        }

        return Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{safeExtension}");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool IsMissingFfmpegTool(Exception exception, string toolName)
    {
        return exception.Message.Contains(toolName, StringComparison.OrdinalIgnoreCase)
               && exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCorruptedModelOutput(AudioProcessingResult result)
    {
        return LooksLikeCorruptedText(result.Transcription)
               || LooksLikeCorruptedText(result.Normalized)
               || LooksLikeCorruptedText(result.Summarized);
    }

    private static bool LooksLikeCorruptedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var collapsed = Regex.Replace(text, "\\s+", " ").Trim();
        if (collapsed.Length == 0)
        {
            return true;
        }

        if (text.Length >= 800 && collapsed.Length < text.Length / 3)
        {
            return true;
        }

        var words = collapsed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length >= 40)
        {
            var distinctWords = words
                .Select(static word => word.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .Count();

            if ((double)distinctWords / words.Length < 0.18d)
            {
                return true;
            }
        }

        var repeatedPhraseMatches = Regex.Matches(
            collapsed,
            "(.{1,80}?)\\1{4,}",
            RegexOptions.IgnoreCase);

        return repeatedPhraseMatches.Count > 0;
    }
}
