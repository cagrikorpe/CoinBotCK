namespace CoinBot.Application.Abstractions.Autonomy;

public interface IWorkerRetryCoordinator
{
    Task<int> RetryAsync(
        string? jobKey,
        string reason,
        CancellationToken cancellationToken = default);
}
