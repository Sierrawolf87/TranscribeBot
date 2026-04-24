using System.ClientModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using TranscribeBot.Interfaces;
using TranscribeBot.Models;
using TranscribeBot.Models.Ai;
using TranscribeBot.Models.Enums;
using TranscribeBot.Options;

namespace TranscribeBot.Services;

public class AiService : IAiService
{
    private const string AudioResponseSchema = """
        {
          "type": "object",
          "properties": {
            "transcription": { "type": ["string", "null"] },
            "normalized": { "type": ["string", "null"] },
            "summarized": { "type": ["string", "null"] }
          },
          "additionalProperties": false
        }
        """;

    private const string CompressionResponseSchema = """
        {
          "type": "object",
          "properties": {
            "compressedText": { "type": ["string", "null"] }
          },
          "additionalProperties": false
        }
        """;

    private readonly OpenAIClient _openAiClient;
    private readonly OpenRouterOptions _openRouterOptions;
    private readonly ILogger<AiService> _logger;

    public AiService(IOptions<OpenRouterOptions> openRouterOptions, ILogger<AiService> logger)
    {
        _openRouterOptions = openRouterOptions.Value;
        _logger = logger;

        _openAiClient = new OpenAIClient(
            credential: new ApiKeyCredential(_openRouterOptions.ApiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(_openRouterOptions.BaseUrl)
            });
    }

    public async Task<AudioProcessingResult> ProcessAudioAsync(
        TranscriptionAiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chatClient = _openAiClient.GetChatClient(_openRouterOptions.ProcessingModel);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(BuildAudioSystemPrompt(request.ChatMode, request.ResponseLanguage))
        };

        var contextBlock = BuildContextBlock(request.ContextMessages);
        if (!string.IsNullOrWhiteSpace(contextBlock))
        {
            messages.Add(new SystemChatMessage(contextBlock));
        }

        var audioBytes = await BinaryData.FromStreamAsync(request.AudioStream, cancellationToken);
        var userInstructions = ChatMessageContentPart.CreateTextPart(
            $"Process the attached audio. Response language: {request.ResponseLanguage}. " +
            $"Return only JSON that matches the schema. Active modes: {FormatModes(request.ChatMode)}.");

        var audioPart = ChatMessageContentPart.CreateInputAudioPart(
            audioBytes,
            ResolveAudioFormat(request.FileName));

        messages.Add(new UserChatMessage(userInstructions, audioPart));

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "transcribe_result",
                jsonSchema: BinaryData.FromBytes(Encoding.UTF8.GetBytes(AudioResponseSchema)),
                jsonSchemaIsStrict: true),
            ReasoningEffortLevel = ResolveReasoningEffortLevel(_openRouterOptions.ReasoningEffort)
        };

        var completionResult = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
        return ParseAudioCompletion(completionResult.Value);
    }

    public async Task<ContextCompressionAiResult> CompressContextAsync(
        CompressContextAiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chatClient = _openAiClient.GetChatClient(_openRouterOptions.CompressionModel);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(BuildCompressionSystemPrompt(request.ResponseLanguage)),
            new UserChatMessage(BuildContextBlock(request.ContextMessages))
        };

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "compressed_context",
                jsonSchema: BinaryData.FromBytes(Encoding.UTF8.GetBytes(CompressionResponseSchema)),
                jsonSchemaIsStrict: true),
            MaxOutputTokenCount = 4096,
            ReasoningEffortLevel = ResolveReasoningEffortLevel(_openRouterOptions.ReasoningEffort)
        };

        var completionResult = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
        return ParseCompressionCompletion(completionResult.Value);
    }

    private AudioProcessingResult ParseAudioCompletion(ChatCompletion completion)
    {
        var root = ParseResponseRoot(completion);
        var usage = completion.Usage;
        var result = new AudioProcessingResult
        {
            Transcription = TryGetOptionalString(root, "transcription"),
            Normalized = TryGetOptionalString(root, "normalized"),
            Summarized = TryGetOptionalString(root, "summarized"),
            InputTokens = usage?.InputTokenCount ?? 0,
            OutputTokens = usage?.OutputTokenCount ?? 0
        };

        _logger.LogInformation(
            "AI audio processing completed. InputTokens={InputTokens}, OutputTokens={OutputTokens}",
            result.InputTokens,
            result.OutputTokens);

        return result;
    }

    private ContextCompressionAiResult ParseCompressionCompletion(ChatCompletion completion)
    {
        var root = ParseResponseRoot(completion);
        var usage = completion.Usage;
        var result = new ContextCompressionAiResult
        {
            CompressedText = TryGetOptionalString(root, "compressedText"),
            InputTokens = usage?.InputTokenCount ?? 0,
            OutputTokens = usage?.OutputTokenCount ?? 0
        };

        _logger.LogInformation(
            "AI context compression completed. InputTokens={InputTokens}, OutputTokens={OutputTokens}",
            result.InputTokens,
            result.OutputTokens);

        return result;
    }

    private static JsonElement ParseResponseRoot(ChatCompletion completion)
    {
        var responseText = string.Concat(
            completion.Content
                .Select(content => content.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

        if (string.IsNullOrWhiteSpace(responseText))
        {
            throw new InvalidOperationException("AI service returned an empty response.");
        }

        using var document = JsonDocument.Parse(responseText);
        return document.RootElement.Clone();
    }

    private static string? TryGetOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => property.ToString()
        };
    }

    private static ChatReasoningEffortLevel ResolveReasoningEffortLevel(string? reasoningEffort)
    {
        return reasoningEffort?.Trim().ToLowerInvariant() switch
        {
            null or "" => ChatReasoningEffortLevel.Medium,
            "none" => ChatReasoningEffortLevel.None,
            "minimal" => ChatReasoningEffortLevel.Minimal,
            "low" => ChatReasoningEffortLevel.Low,
            "medium" => ChatReasoningEffortLevel.Medium,
            "high" => ChatReasoningEffortLevel.High,
            _ => throw new InvalidOperationException(
                $"Unsupported OpenRouter reasoning effort '{reasoningEffort}'. Supported values: none, minimal, low, medium, high.")
        };
    }

    private static string BuildAudioSystemPrompt(ChatMode chatMode, string responseLanguage)
    {
        var actions = new List<string>();

        if (chatMode.HasFlag(ChatMode.Transcribe))
        {
            actions.Add("produce an accurate transcription of the audio");
        }

        if (chatMode.HasFlag(ChatMode.Normalize))
        {
            actions.Add("rewrite the transcript into clean, readable text; remove filler words, false starts, repeated fragments, meaningless phrases, interjections, and conversational noise; preserve the meaning, facts, speaker intent, and important details");
        }

        if (chatMode.HasFlag(ChatMode.Summarize))
        {
            actions.Add("create a detailed summary that preserves all important facts, relationships, participants, examples, reasons, and conclusions");
        }

        if (actions.Count == 0)
        {
            actions.Add("produce an accurate transcription of the audio");
        }

        return
            "You process user voice messages. " +
            $"Your task is to {string.Join("; ", actions)}. " +
            $"Write the content of returned fields in '{responseLanguage}'. " +
            "Fill only the fields that correspond to the selected modes. " +
            "Return null for fields that were not requested. " +
            "For transcription, if the audio clearly contains multiple different speakers, separate their utterances with labels such as 'Speaker 1', 'Speaker 2', and so on. " +
            "Use a real person's name only if the speaker identity is explicitly clear from the audio or context. " +
            "Do not guess or assign names just because a name was mentioned in the conversation. " +
            "For normalization, do not merely add punctuation to the transcript. Rewrite it into natural written language. " +
            "Remove filler words and phrases such as 'well', 'like', 'basically', 'you know', 'so', 'uh', 'um', 'kind of', and their equivalents in the source language. " +
            "Remove meaningless standalone fragments such as 'that's it', 'like this', 'ah, like that', unless they carry real meaning. " +
            "Keep domain terms, product names, quantities, people, and ownership/relationship details. " +
            "Example: turn 'Tesla gives you, well, Tesla itself in the app, on Supercharger, like, gives points' into 'Tesla gives you points in the app that can be used on Superchargers'. " +
            "For summaries, be detailed enough that a reader understands who did what and why. " +
            "Do not omit relevant people or relationships, for example a brother, friend, owner, buyer, or speaker, if they explain the situation. " +
            "The summary must be grounded in the message facts and must not drop key details.";
    }

    private static string BuildCompressionSystemPrompt(string responseLanguage)
    {
        return
            "You compress a user's message history to reduce token usage. " +
            $"Return the result in '{responseLanguage}'. " +
            "Preserve facts, agreements, entities, names, numbers, deadlines, and important context. " +
            "Remove repetition and secondary details. " +
            "Return only JSON with the compressedText field.";
    }

    private static string BuildContextBlock(IReadOnlyCollection<AiRequestContextMessage> contextMessages)
    {
        if (contextMessages.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Previous user message history:");

        foreach (var message in contextMessages.OrderBy(item => item.CreatedAt))
        {
            builder
                .Append('[')
                .Append(message.CreatedAt.ToString("u"))
                .AppendLine("]")
                .AppendLine(message.Content)
                .AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatModes(ChatMode chatMode)
    {
        var modes = new List<string>();

        if (chatMode.HasFlag(ChatMode.Transcribe))
        {
            modes.Add("transcription");
        }

        if (chatMode.HasFlag(ChatMode.Normalize))
        {
            modes.Add("normalization");
        }

        if (chatMode.HasFlag(ChatMode.Summarize))
        {
            modes.Add("summary");
        }

        return modes.Count == 0 ? "transcription" : string.Join(", ", modes);
    }

    private static ChatInputAudioFormat ResolveAudioFormat(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".wav" => ChatInputAudioFormat.Wav,
            ".mp3" => ChatInputAudioFormat.Mp3,
            _ => ChatInputAudioFormat.Mp3
        };
    }
}
