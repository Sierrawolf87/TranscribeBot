namespace TranscribeBot.Models;

public class ContextSummarySourceMessage
{
    public DateTime CreatedAt { get; set; }
    public string? Transcription { get; set; }
    public string? CompressedText { get; set; }
}
