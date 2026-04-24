using TranscribeBot.Models;
using TranscribeBot.Models.Enums;

namespace TranscribeBot.Interfaces;

public interface ITranscribeService
{
    Task<User> GetOrCreateUserAsync(long telegramUserId, string? username, CancellationToken cancellationToken = default);
    Task<UserSettings> ToggleChatModeAsync(long telegramUserId, string? username, ChatMode mode, CancellationToken cancellationToken = default);
    Task<UserSettings> ToggleUseContextAsync(long telegramUserId, string? username, CancellationToken cancellationToken = default);
    Task<UserSettings> SetLanguageAsync(long telegramUserId, string? username, string language, CancellationToken cancellationToken = default);
    Task<int> ResetContextAsync(long telegramUserId, string? username, CancellationToken cancellationToken = default);
    Task<CompressionResult> CompressContextAsync(long telegramUserId, string? username, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<string>> ProcessAudioAsync(AudioTranscriptionRequest request, CancellationToken cancellationToken = default);
    Task<bool> IsUserAllowedAsync(long telegramUserId, CancellationToken cancellationToken = default);
    Task<bool> AddAllowedUserAsync(long telegramUserId, long addedByTelegramUserId, CancellationToken cancellationToken = default);
    Task<bool> RemoveAllowedUserAsync(long telegramUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<long>> GetAllowedUsersAsync(CancellationToken cancellationToken = default);
}
