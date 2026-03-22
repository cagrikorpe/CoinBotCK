namespace CoinBot.Infrastructure.Jobs;

public interface IDistributedJobLockManager
{
    Task<bool> TryAcquireAsync(string jobKey, string jobType, CancellationToken cancellationToken = default);

    Task<bool> RenewAsync(string jobKey, CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(string jobKey, CancellationToken cancellationToken = default);
}
