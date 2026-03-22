namespace CoinBot.Web.ViewModels.Admin;

public sealed class AdminSecurityHelpCardViewModel
{
    public string Eyebrow { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string[] Items { get; init; } = [];
    public string? BadgeLabel { get; init; }
    public string? BadgeTone { get; init; }
    public string? ActionText { get; init; }
    public string? ActionHref { get; init; }
}
