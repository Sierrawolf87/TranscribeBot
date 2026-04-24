using TranscribeBot.Models.Base;
using TranscribeBot.Models.Enums;

namespace TranscribeBot.Models;

public class UserSettings : BaseEntity
{
    public string? Language { get; set; } = "ru";
    public ChatMode ChatMode { get; set; } = ChatMode.Transcribe;
    public bool UseContext { get; set; } = false;

    public int UserId { get; set; }
    public User? User { get; set; }
}