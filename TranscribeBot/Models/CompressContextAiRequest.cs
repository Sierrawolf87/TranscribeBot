namespace TranscribeBot.Models;

public class CompressContextAiRequest
{
    public string ResponseLanguage { get; set; } = "ru";
    public IReadOnlyCollection<AiRequestContextMessage> ContextMessages { get; set; } = [];
}
