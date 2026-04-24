namespace TranscribeBot.Models;

public class CompressionResult
{
    public bool WasCompressed { get; set; }
    public int RemovedMessagesCount { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public string Message { get; set; } = string.Empty;
}
