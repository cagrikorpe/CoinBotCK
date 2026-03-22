namespace CoinBot.Application.Abstractions.Execution;

public interface ITradingModeResolver
{
    Task<TradingModeResolution> ResolveAsync(
        TradingModeResolutionRequest request,
        CancellationToken cancellationToken = default);
}
