namespace TranscribeBot.Models;

public class AudioTranscriptionRequest
{
    public long TelegramUserId { get; set; }
    public string? Username { get; set; }
    public Stream AudioStream { get; set; } = Stream.Null;
    public string FileName { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
}
