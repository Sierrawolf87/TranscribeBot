using System.Collections.Concurrent;
using System.Threading.Channels;
using TranscribeBot.Interfaces;

namespace TranscribeBot.Services;

public class UserProcessingQueue(ILogger<UserProcessingQueue> logger) : IUserProcessingQueue
{
    private const int MaxConcurrentGlobalJobs = 2;

    private readonly ConcurrentDictionary<long, UserQueue> _queues = new();
    private readonly SemaphoreSlim _globalSlot = new(MaxConcurrentGlobalJobs, MaxConcurrentGlobalJobs);

    public async Task<T> ExecuteAsync<T>(
        long userId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var completionSource = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        await EnqueueAsync(
            userId,
            async token =>
            {
                try
                {
                    completionSource.TrySetResult(await action(token));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    completionSource.TrySetCanceled(token);
                }
                catch (Exception exception)
                {
                    completionSource.TrySetException(exception);
                }
            },
            requiresGlobalSlot: false,
            preserveUserOrder: true,
            cancellationToken);

        return await completionSource.Task.WaitAsync(cancellationToken);
    }

    public async Task EnqueueAsync(
        long userId,
        Func<CancellationToken, Task> action,
        bool requiresGlobalSlot,
        bool preserveUserOrder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!preserveUserOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _ = Task.Run(
                () => ExecuteQueuedJobAsync(
                    userId,
                    new QueuedJob(action, requiresGlobalSlot, CancellationToken.None)),
                CancellationToken.None);

            return;
        }

        var queue = _queues.GetOrAdd(userId, _ => CreateUserQueue(userId));
        await queue.Writer.WriteAsync(new QueuedJob(action, requiresGlobalSlot, cancellationToken), cancellationToken);
    }

    private UserQueue CreateUserQueue(long userId)
    {
        var queue = new UserQueue();
        _ = Task.Run(() => ProcessUserQueueAsync(userId, queue), CancellationToken.None);
        return queue;
    }

    private async Task ProcessUserQueueAsync(long userId, UserQueue queue)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            await ExecuteQueuedJobAsync(userId, job);
        }
    }

    private async Task ExecuteQueuedJobAsync(long userId, QueuedJob job)
    {
        try
        {
            if (job.RequiresGlobalSlot)
            {
                await _globalSlot.WaitAsync(job.CancellationToken);
            }

            try
            {
                await job.Action(job.CancellationToken);
            }
            finally
            {
                if (job.RequiresGlobalSlot)
                {
                    _globalSlot.Release();
                }
            }
        }
        catch (OperationCanceledException) when (job.CancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Queued job was canceled for TelegramUserId={TelegramUserId}.", userId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Queued job failed for TelegramUserId={TelegramUserId}.", userId);
        }
    }

    private sealed class UserQueue
    {
        private readonly Channel<QueuedJob> _channel = Channel.CreateUnbounded<QueuedJob>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        public ChannelReader<QueuedJob> Reader => _channel.Reader;

        public ChannelWriter<QueuedJob> Writer => _channel.Writer;
    }

    private sealed record QueuedJob(
        Func<CancellationToken, Task> Action,
        bool RequiresGlobalSlot,
        CancellationToken CancellationToken);
}
