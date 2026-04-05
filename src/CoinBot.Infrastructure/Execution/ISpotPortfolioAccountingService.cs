using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Exchange;

namespace CoinBot.Infrastructure.Execution;

public interface ISpotPortfolioAccountingService
{
    Task<SpotPortfolioApplyResult?> ApplyAsync(
        ExecutionOrder order,
        BinanceOrderStatusSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
