namespace TranscribeBot.Options;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string ProcessingModel { get; set; } = "google/gemini-3.1-flash-lite-preview";
    public string CompressionModel { get; set; } = "google/gemini-3-flash-preview";
    public string ReasoningEffort { get; set; } = "medium";
}
