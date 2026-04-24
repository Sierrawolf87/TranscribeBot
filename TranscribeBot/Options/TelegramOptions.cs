namespace TranscribeBot.Options;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string Token { get; set; } = string.Empty;
    public string AllowedUserIds { get; set; } = string.Empty;
}
