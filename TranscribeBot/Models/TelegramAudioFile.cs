namespace TranscribeBot.Models;

public class TelegramAudioFile : IDisposable
{
    public Stream Content { get; set; } = Stream.Null;
    public string FileName { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }

    public void Dispose()
    {
        Content.Dispose();
    }
}
