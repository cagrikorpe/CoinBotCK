namespace CoinBot.Application.Abstractions.Strategies;

public interface IStrategyTemplateCatalogService
{
    Task<IReadOnlyCollection<StrategyTemplateSnapshot>> ListAsync(CancellationToken cancellationToken = default);

    Task<StrategyTemplateSnapshot> GetAsync(string templateKey, CancellationToken cancellationToken = default);
}
