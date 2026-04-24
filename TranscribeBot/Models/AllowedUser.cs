using TranscribeBot.Models.Base;

namespace TranscribeBot.Models;

public class AllowedUser : BaseEntity
{
    public long TelegramId { get; set; }
    public long? AddedByTelegramId { get; set; }
}
