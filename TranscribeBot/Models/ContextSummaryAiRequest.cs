namespace TranscribeBot.Models;

public class ContextSummaryAiRequest
{
    public string ResponseLanguage { get; set; } = "ru";
    public IReadOnlyCollection<ContextSummarySourceMessage> Messages { get; set; } = [];
}
