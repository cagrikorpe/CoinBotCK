using CoinBot.Application.Abstractions.Strategies;

namespace CoinBot.Web.ViewModels.Admin;

public sealed record AdminStrategyTemplateCatalogPageViewModel(
    string? SelectedTemplateKey,
    IReadOnlyCollection<StrategyTemplateSnapshot> Templates,
    StrategyTemplateSnapshot? SelectedTemplate,
    IReadOnlyCollection<StrategyTemplateRevisionSnapshot> SelectedTemplateRevisions,
    bool CanManageTemplates,
    DateTime LastRefreshedAtUtc,
    AdminStrategyTemplateBuilderDraftViewModel? BuilderDraft = null);

public sealed record AdminStrategyTemplateBuilderDraftViewModel(
    string? SourceTemplateKey,
    string? TemplateKey,
    string? TemplateName,
    string? Description,
    string? Category,
    string? DefinitionJson)
{
    public bool HasSourceTemplate => !string.IsNullOrWhiteSpace(SourceTemplateKey);
}
