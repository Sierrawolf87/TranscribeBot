using System.ClientModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using Polly;
using TranscribeBot.Interfaces;
using TranscribeBot.Models;
using TranscribeBot.Models.Ai;
using TranscribeBot.Models.Enums;
using TranscribeBot.Options;

namespace TranscribeBot.Services;

public class AiService : IAiService
{
    private static readonly AsyncPolicy AiRetryPolicy = Policy
        .Handle<ClientResultException>(IsTransientAiException)
        .Or<HttpRequestException>()
        .Or<TimeoutException>()
        .WaitAndRetryAsync(3, GetAiRetryDelay);

    private readonly OpenAIClient _openAiClient;
    private readonly OpenRouterOptions _openRouterOptions;
    private readonly ILogger<AiService> _logger;

    public AiService(IOptions<OpenRouterOptions> openRouterOptions, ILogger<AiService> logger)
    {
        _openRouterOptions = NormalizeOptions(openRouterOptions.Value);
        _logger = logger;

        _openAiClient = new OpenAIClient(
            credential: new ApiKeyCredential(_openRouterOptions.ApiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(_openRouterOptions.BaseUrl)
            });
    }

    private static OpenRouterOptions NormalizeOptions(OpenRouterOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            options.BaseUrl = OpenRouterOptions.DefaultBaseUrl;
        }

        if (string.IsNullOrWhiteSpace(options.ProcessingModel))
        {
            options.ProcessingModel = OpenRouterOptions.DefaultProcessingModel;
        }

        if (string.IsNullOrWhiteSpace(options.CompressionModel))
        {
            options.CompressionModel = OpenRouterOptions.DefaultCompressionModel;
        }

        if (string.IsNullOrWhiteSpace(options.ReasoningEffort))
        {
            options.ReasoningEffort = OpenRouterOptions.DefaultReasoningEffort;
        }

        return options;
    }

    public async Task<AudioProcessingResult> ProcessAudioAsync(
        TranscriptionAiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chatClient = _openAiClient.GetChatClient(_openRouterOptions.ProcessingModel);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(BuildAudioSystemPrompt(
                request.ChatMode,
                request.ResponseLanguage))
        };

        var contextBlock = BuildContextBlock(request.ContextMessages);
        if (!string.IsNullOrWhiteSpace(contextBlock))
        {
            messages.Add(new SystemChatMessage(contextBlock));
        }

        var audioBytes = await BinaryData.FromStreamAsync(request.AudioStream, cancellationToken);
        var responseFields = GetAudioResponseFields(request.ChatMode);
        var userInstructions = ChatMessageContentPart.CreateTextPart(
            $"Process the attached audio. Response language: {request.ResponseLanguage}. " +
            $"Return only a JSON object with these required fields: {FormatFieldList(responseFields)}. " +
            $"Active modes: {FormatModes(request.ChatMode)}.");

        var audioPart = ChatMessageContentPart.CreateInputAudioPart(
            audioBytes,
            ResolveAudioFormat(request.FileName));

        messages.Add(new UserChatMessage(userInstructions, audioPart));

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
            ReasoningEffortLevel = ResolveReasoningEffortLevel(_openRouterOptions.ReasoningEffort)
        };

        var completionResult = await AiRetryPolicy.ExecuteAsync(
            token => chatClient.CompleteChatAsync(messages, options, token),
            cancellationToken);
        return ParseAudioCompletion(completionResult.Value, responseFields);
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
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
            MaxOutputTokenCount = 4096,
            ReasoningEffortLevel = ResolveReasoningEffortLevel(_openRouterOptions.ReasoningEffort)
        };

        var completionResult = await AiRetryPolicy.ExecuteAsync(
            token => chatClient.CompleteChatAsync(messages, options, token),
            cancellationToken);
        return ParseCompressionCompletion(completionResult.Value);
    }

    public async Task<ContextSummaryAiResult> SummarizeContextAsync(
        ContextSummaryAiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chatClient = _openAiClient.GetChatClient(_openRouterOptions.CompressionModel);
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(BuildContextSummarySystemPrompt(request.ResponseLanguage)),
            new UserChatMessage(BuildContextSummaryBlock(request.Messages))
        };

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
            ReasoningEffortLevel = ResolveReasoningEffortLevel(_openRouterOptions.ReasoningEffort)
        };

        var completionResult = await AiRetryPolicy.ExecuteAsync(
            token => chatClient.CompleteChatAsync(messages, options, token),
            cancellationToken);
        return ParseContextSummaryCompletion(completionResult.Value);
    }

    private AudioProcessingResult ParseAudioCompletion(
        ChatCompletion completion,
        IReadOnlyCollection<string> requiredFields)
    {
        var root = ParseResponseRoot(completion);
        var usage = completion.Usage;
        var requiredFieldSet = requiredFields.ToHashSet(StringComparer.Ordinal);

        var result = new AudioProcessingResult
        {
            Transcription = requiredFieldSet.Contains("transcription")
                ? GetRequiredString(root, "transcription")
                : null,
            Normalized = requiredFieldSet.Contains("normalized")
                ? GetRequiredString(root, "normalized")
                : null,
            Summarized = requiredFieldSet.Contains("summarized")
                ? GetRequiredString(root, "summarized")
                : null,
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
            CompressedText = GetRequiredString(root, "compressedText"),
            InputTokens = usage?.InputTokenCount ?? 0,
            OutputTokens = usage?.OutputTokenCount ?? 0
        };

        _logger.LogInformation(
            "AI context compression completed. InputTokens={InputTokens}, OutputTokens={OutputTokens}",
            result.InputTokens,
            result.OutputTokens);

        return result;
    }

    private ContextSummaryAiResult ParseContextSummaryCompletion(ChatCompletion completion)
    {
        var root = ParseResponseRoot(completion);
        var usage = completion.Usage;
        var result = new ContextSummaryAiResult
        {
            Summary = GetRequiredString(root, "summary"),
            InputTokens = usage?.InputTokenCount ?? 0,
            OutputTokens = usage?.OutputTokenCount ?? 0
        };

        _logger.LogInformation(
            "AI context summary completed. InputTokens={InputTokens}, OutputTokens={OutputTokens}",
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

        responseText = NormalizeJsonObjectResponse(responseText);

        using var document = JsonDocument.Parse(responseText);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("AI service returned JSON, but the root value is not an object.");
        }

        return document.RootElement.Clone();
    }

    private static string NormalizeJsonObjectResponse(string responseText)
    {
        var trimmed = responseText.Trim().TrimStart('\uFEFF');
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = StripMarkdownFence(trimmed);
        }

        if (trimmed.StartsWith('{'))
        {
            return ExtractFirstJsonObject(trimmed) ?? trimmed;
        }

        var jsonObject = ExtractFirstJsonObject(trimmed);
        if (jsonObject is not null)
        {
            return jsonObject;
        }

        throw new InvalidOperationException("AI service returned a response that does not contain a JSON object.");
    }

    private static string StripMarkdownFence(string responseText)
    {
        var firstLineEnd = responseText.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return responseText;
        }

        var contentStart = firstLineEnd + 1;
        var closingFenceStart = responseText.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceStart <= contentStart)
        {
            return responseText[contentStart..].Trim();
        }

        return responseText[contentStart..closingFenceStart].Trim();
    }

    private static string? ExtractFirstJsonObject(string responseText)
    {
        var start = responseText.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var isEscaped = false;

        for (var index = start; index < responseText.Length; index++)
        {
            var character = responseText[index];

            if (inString)
            {
                if (isEscaped)
                {
                    isEscaped = false;
                }
                else if (character == '\\')
                {
                    isEscaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return responseText[start..(index + 1)];
                }
            }
        }

        return null;
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException($"AI service response does not contain required field '{propertyName}'.");
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"AI service response field '{propertyName}' must be a string.");
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"AI service response field '{propertyName}' is empty.");
        }

        return value;
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

    private static bool IsTransientAiException(ClientResultException exception)
    {
        return exception.Status is 408 or 429 or 500 or 502 or 503 or 504;
    }

    private static TimeSpan GetAiRetryDelay(int retryAttempt)
    {
        var delaySeconds = Math.Min(10, Math.Pow(2, retryAttempt - 1));
        var jitterMilliseconds = Random.Shared.Next(100, 700);
        return TimeSpan.FromSeconds(delaySeconds) + TimeSpan.FromMilliseconds(jitterMilliseconds);
    }

    private static string BuildAudioSystemPrompt(
        ChatMode chatMode,
        string responseLanguage)
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
            $"Return only a JSON object with exactly these required fields: {FormatFieldList(GetAudioResponseFields(chatMode))}. " +
            "Do not wrap the JSON object in Markdown fences or any other text. " +
            "Every required field must be a non-empty string. Do not return null, empty strings, or omit required fields. " +
            "Do not include fields that are not listed as required. " +
            $"If the audio contains no speech or speech cannot be transcribed, fill every required field with a sentence in '{responseLanguage}' meaning: 'There is no speech in this audio, so transcription is impossible'. " +
            "Markdown formatting is allowed in returned text. Use standard Markdown for simple formatting. " +
            "Use '**bold**', '*italic*', '`code`', fenced code blocks, '[text](url)', '# headings', and flat lists with '-' or '1.'. " +
            "Do not use tables or nested lists. " +
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
            "Markdown formatting is allowed in returned text. Use standard Markdown for simple formatting. " +
            "Use '**bold**', '*italic*', '`code`', fenced code blocks, '[text](url)', '# headings', and flat lists with '-' or '1.'. " +
            "Do not use tables or nested lists. " +
            "Preserve facts, agreements, entities, names, numbers, deadlines, and important context. " +
            "Remove repetition and secondary details. " +
            "Return only a JSON object with exactly one required field: \"compressedText\". " +
            "Do not wrap the JSON object in Markdown fences or any other text. " +
            "The compressedText field must be a non-empty string.";
    }

    private static string BuildContextSummarySystemPrompt(string responseLanguage)
    {
        return
            "You create a detailed summary of the user's saved message context. " +
            $"Return the result in '{responseLanguage}'. " +
            "Markdown formatting is allowed in returned text. Use standard Markdown for simple formatting. " +
            "Use '**bold**', '*italic*', '`code`', fenced code blocks, '[text](url)', '# headings', and flat lists with '-' or '1.'. " +
            "Do not use tables or nested lists. " +
            "You will receive full transcriptions for context messages and, for some entries, an additional compressed context snapshot. " +
            "Use the full transcriptions as the primary source of truth. " +
            "Use compressed context only as supporting memory and to preserve older facts if they are consistent with the transcriptions. " +
            "Produce a detailed summary with clear topic sections. " +
            "If the messages contain unrelated discussions, separate them into distinct topics. " +
            "For each topic, preserve participants, decisions, facts, dates, numbers, reasons, dependencies, open questions, and action items when present. " +
            "Do not invent information and do not merge unrelated topics. " +
            "Return only a JSON object with exactly one required field: \"summary\". " +
            "Do not wrap the JSON object in Markdown fences or any other text. " +
            "The summary field must be a non-empty string.";
    }

    private static IReadOnlyList<string> GetAudioResponseFields(ChatMode chatMode)
    {
        var fields = new List<string>();

        if (chatMode.HasFlag(ChatMode.Transcribe))
        {
            fields.Add("transcription");
        }

        if (chatMode.HasFlag(ChatMode.Normalize))
        {
            fields.Add("normalized");
        }

        if (chatMode.HasFlag(ChatMode.Summarize))
        {
            fields.Add("summarized");
        }

        if (fields.Count == 0)
        {
            fields.Add("transcription");
        }

        return fields;
    }

    private static string FormatFieldList(IReadOnlyCollection<string> fields)
    {
        return string.Join(", ", fields.Select(field => $"\"{field}\""));
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

    private static string BuildContextSummaryBlock(IReadOnlyCollection<ContextSummarySourceMessage> messages)
    {
        if (messages.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Saved context messages:");

        foreach (var message in messages.OrderBy(item => item.CreatedAt))
        {
            builder
                .Append('[')
                .Append(message.CreatedAt.ToString("u"))
                .AppendLine("]");

            if (!string.IsNullOrWhiteSpace(message.Transcription))
            {
                builder
                    .AppendLine("Full transcription:")
                    .AppendLine(message.Transcription);
            }

            if (!string.IsNullOrWhiteSpace(message.CompressedText))
            {
                builder
                    .AppendLine("Compressed context snapshot:")
                    .AppendLine(message.CompressedText);
            }

            builder.AppendLine();
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
