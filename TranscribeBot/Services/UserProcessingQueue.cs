using System.Collections.Concurrent;
using TranscribeBot.Interfaces;

namespace TranscribeBot.Services;

public class UserProcessingQueue : IUserProcessingQueue
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    public async Task<T> ExecuteAsync<T>(
        long userId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var semaphore = _locks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);

        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
