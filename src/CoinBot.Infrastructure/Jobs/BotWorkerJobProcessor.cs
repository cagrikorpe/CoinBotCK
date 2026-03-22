using CoinBot.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BotWorkerJobProcessor(ILogger<BotWorkerJobProcessor> logger) : IBotWorkerJobProcessor
{
    public Task<BackgroundJobProcessResult> ProcessAsync(
        TradingBot bot,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogInformation(
            "Bot orchestration processor completed dry-run cycle for BotId {BotId} with idempotency key {IdempotencyKey}.",
            bot.Id,
            idempotencyKey);

        return Task.FromResult(BackgroundJobProcessResult.Success());
    }
}
