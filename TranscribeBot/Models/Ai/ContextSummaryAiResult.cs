namespace TranscribeBot.Models.Ai;

public class ContextSummaryAiResult
{
    public string? Summary { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
