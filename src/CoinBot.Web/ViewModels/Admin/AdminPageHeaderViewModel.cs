namespace CoinBot.Web.ViewModels.Admin;

public sealed class AdminPageHeaderViewModel
{
    public string Eyebrow { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public AdminBadgeViewModel? StatusBadge { get; init; }
    public IReadOnlyList<AdminBadgeViewModel> MetaBadges { get; init; } = Array.Empty<AdminBadgeViewModel>();
    public string? PrimaryActionText { get; init; }
    public string? PrimaryActionHref { get; init; }
    public string? SecondaryActionText { get; init; }
    public string? SecondaryActionHref { get; init; }
}
