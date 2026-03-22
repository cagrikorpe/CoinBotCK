namespace CoinBot.Application.Abstractions.Strategies;

public interface IStrategyVersionService
{
    Task<StrategyVersionSnapshot> CreateDraftAsync(
        Guid strategyId,
        string definitionJson,
        CancellationToken cancellationToken = default);

    Task<StrategyVersionSnapshot> PublishAsync(
        Guid strategyVersionId,
        CancellationToken cancellationToken = default);

    Task<StrategyVersionSnapshot> ArchiveAsync(
        Guid strategyVersionId,
        CancellationToken cancellationToken = default);
}
