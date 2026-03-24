namespace CoinBot.Application.Abstractions.Administration;

public interface ILogCenterRetentionService
{
    Task<LogCenterRetentionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<LogCenterRetentionRunSnapshot> ApplyAsync(CancellationToken cancellationToken = default);
}
