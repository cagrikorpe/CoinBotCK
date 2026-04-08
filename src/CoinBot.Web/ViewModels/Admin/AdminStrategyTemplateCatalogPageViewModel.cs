using CoinBot.Application.Abstractions.Strategies;

namespace CoinBot.Web.ViewModels.Admin;

public sealed record AdminStrategyTemplateCatalogPageViewModel(
    string? SelectedTemplateKey,
    IReadOnlyCollection<StrategyTemplateSnapshot> Templates,
    StrategyTemplateSnapshot? SelectedTemplate,
    IReadOnlyCollection<StrategyTemplateRevisionSnapshot> SelectedTemplateRevisions,
    bool CanManageTemplates,
    DateTime LastRefreshedAtUtc);
