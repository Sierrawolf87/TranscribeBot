using TranscribeBot.Models.Base;

namespace TranscribeBot.Models;

public class Messages : BaseEntity
{
    public string? Transcription { get; set; }
    public string? Normalized { get; set; }
    public string? Summarized { get; set; }
    public string? CompressedText { get; set; }

    public int InputTokens { get; set; } = 0;
    public int OutputTokens { get; set; } = 0;

    public int UserId { get; set; }
    public User? User { get; set; }
}