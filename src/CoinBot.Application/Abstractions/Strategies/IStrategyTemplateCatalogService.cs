namespace CoinBot.Application.Abstractions.Strategies;

public interface IStrategyTemplateCatalogService
{
    Task<IReadOnlyCollection<StrategyTemplateSnapshot>> ListAsync(CancellationToken cancellationToken = default);

    Task<StrategyTemplateSnapshot> GetAsync(string templateKey, CancellationToken cancellationToken = default);

    Task<StrategyTemplateSnapshot> CreateCustomAsync(
        string ownerUserId,
        string templateKey,
        string templateName,
        string description,
        string category,
        string definitionJson,
        CancellationToken cancellationToken = default);

    Task<StrategyTemplateSnapshot> CloneAsync(
        string ownerUserId,
        string sourceTemplateKey,
        string templateKey,
        string templateName,
        string description,
        string category,
        CancellationToken cancellationToken = default);

    Task<StrategyTemplateSnapshot> ArchiveAsync(
        string templateKey,
        CancellationToken cancellationToken = default);
}
