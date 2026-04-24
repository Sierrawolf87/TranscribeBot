using TranscribeBot.Models.Enums;

namespace TranscribeBot.Models;

public class TranscriptionAiRequest
{
    public Stream AudioStream { get; set; } = Stream.Null;
    public string FileName { get; set; } = string.Empty;
    public string ResponseLanguage { get; set; } = "ru";
    public ChatMode ChatMode { get; set; } = ChatMode.Transcribe;
    public IReadOnlyCollection<AiRequestContextMessage> ContextMessages { get; set; } = [];
}
