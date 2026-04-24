namespace TranscribeBot.Models.Ai;

public class ContextCompressionAiResult
{
    public string? CompressedText { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
