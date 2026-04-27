using TranscribeBot.Models;
using TranscribeBot.Models.Ai;

namespace TranscribeBot.Interfaces;

public interface IAiService
{
    Task<AudioProcessingResult> ProcessAudioAsync(TranscriptionAiRequest request, CancellationToken cancellationToken = default);
    Task<ContextCompressionAiResult> CompressContextAsync(CompressContextAiRequest request, CancellationToken cancellationToken = default);
    Task<ContextSummaryAiResult> SummarizeContextAsync(ContextSummaryAiRequest request, CancellationToken cancellationToken = default);
}
