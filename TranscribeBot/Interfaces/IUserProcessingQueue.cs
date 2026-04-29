namespace TranscribeBot.Interfaces;

public interface IUserProcessingQueue
{
    Task<T> ExecuteAsync<T>(long userId, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);
    Task EnqueueAsync(long userId, Func<CancellationToken, Task> action, bool requiresGlobalSlot, bool preserveUserOrder, CancellationToken cancellationToken = default);
}
