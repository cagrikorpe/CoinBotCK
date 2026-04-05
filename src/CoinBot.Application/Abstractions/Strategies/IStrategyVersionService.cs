namespace CoinBot.Application.Abstractions.Strategies;

public interface IStrategyVersionService
{
    Task<StrategyVersionSnapshot> CreateDraftAsync(
        Guid strategyId,
        string definitionJson,
        CancellationToken cancellationToken = default);

    Task<StrategyVersionSnapshot> CreateDraftFromTemplateAsync(
        Guid strategyId,
        string templateKey,
        CancellationToken cancellationToken = default);

    Task<StrategyVersionSnapshot> CreateDraftFromVersionAsync(
        Guid strategyVersionId,
        CancellationToken cancellationToken = default);

    Task<StrategyVersionSnapshot> PublishAsync(
        Guid strategyVersionId,
        CancellationToken cancellationToken = default);

    Task<StrategyVersionSnapshot> ActivateAsync(
        Guid strategyVersionId,
        string? expectedActivationToken = null,
        CancellationToken cancellationToken = default);

    Task<StrategyVersionSnapshot?> DeactivateAsync(
        Guid strategyId,
        string? expectedActivationToken = null,
        CancellationToken cancellationToken = default);

    Task<StrategyVersionSnapshot> ArchiveAsync(
        Guid strategyVersionId,
        CancellationToken cancellationToken = default);
}
