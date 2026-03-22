using CoinBot.Domain.Entities;

namespace CoinBot.Infrastructure.Jobs;

public interface IBotWorkerJobProcessor
{
    Task<BackgroundJobProcessResult> ProcessAsync(
        TradingBot bot,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}
