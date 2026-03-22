namespace CoinBot.Web.ViewModels.Admin;

public sealed class AdminPlaceholderPageViewModel
{
    public string Eyebrow { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string HintTitle { get; init; } = string.Empty;
    public string HintMessage { get; init; } = string.Empty;
    public string? PrimaryActionText { get; init; }
    public string? PrimaryActionHref { get; init; }
    public string? SecondaryActionText { get; init; }
    public string? SecondaryActionHref { get; init; }
    public AdminBadgeViewModel? StatusBadge { get; init; }
    public AdminInfoStripViewModel? Strip { get; init; }
}
