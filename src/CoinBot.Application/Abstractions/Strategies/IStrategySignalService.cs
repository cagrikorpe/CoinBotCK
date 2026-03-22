namespace CoinBot.Application.Abstractions.Strategies;

public interface IStrategySignalService
{
    Task<StrategySignalGenerationResult> GenerateAsync(
        GenerateStrategySignalsRequest request,
        CancellationToken cancellationToken = default);

    Task<StrategySignalSnapshot?> GetAsync(
        Guid strategySignalId,
        CancellationToken cancellationToken = default);

    Task<StrategySignalVetoSnapshot?> GetVetoAsync(
        Guid strategySignalVetoId,
        CancellationToken cancellationToken = default);
}
