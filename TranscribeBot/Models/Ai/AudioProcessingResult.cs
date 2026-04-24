namespace TranscribeBot.Models.Ai;

public class AudioProcessingResult
{
    public string? Transcription { get; set; }
    public string? Normalized { get; set; }
    public string? Summarized { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
