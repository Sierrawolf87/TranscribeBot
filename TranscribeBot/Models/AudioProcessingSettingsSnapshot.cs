using TranscribeBot.Models.Enums;

namespace TranscribeBot.Models;

public sealed record AudioProcessingSettingsSnapshot(
    string Language,
    ChatMode ChatMode,
    bool UseContext)
{
    public static AudioProcessingSettingsSnapshot Default { get; } = new("ru", ChatMode.Transcribe, false);

    public static AudioProcessingSettingsSnapshot From(UserSettings settings)
    {
        return new AudioProcessingSettingsSnapshot(
            string.IsNullOrWhiteSpace(settings.Language) ? "ru" : settings.Language,
            settings.ChatMode,
            settings.UseContext);
    }
}
