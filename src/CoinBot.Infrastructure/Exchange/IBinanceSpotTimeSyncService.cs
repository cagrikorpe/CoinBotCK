using CoinBot.Application.Abstractions.Exchange;

namespace CoinBot.Infrastructure.Exchange;

public interface IBinanceSpotTimeSyncService
{
    Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default);
}
