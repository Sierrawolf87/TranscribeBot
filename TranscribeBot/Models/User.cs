using TranscribeBot.Models.Base;

namespace TranscribeBot.Models;

public class User : BaseEntity
{
    public string? Username { get; set; }
    public long TelegramId { get; set; }
    public int TotalTokenUsage { get; set; } = 0;

    public UserSettings Settings { get; set; } = new UserSettings();
}