namespace TranscribeBot.Models.Enums;

[Flags]
public enum ChatMode
{
    Transcribe = 1,
    Normalize = 2,
    Summarize = 4
}