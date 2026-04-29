namespace TranscribeBot.Options;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";
    public const string DefaultProcessingModel = "google/gemini-3.1-flash-lite-preview";
    public const string DefaultCompressionModel = "google/gemini-3-flash-preview";
    public const string DefaultReasoningEffort = "medium";

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string ApiKey { get; set; } = string.Empty;
    public string ProcessingModel { get; set; } = DefaultProcessingModel;
    public string CompressionModel { get; set; } = DefaultCompressionModel;
    public string ReasoningEffort { get; set; } = DefaultReasoningEffort;
}
