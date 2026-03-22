namespace CoinBot.Web.ViewModels.Admin;

public sealed class AdminBadgeViewModel
{
    public string Label { get; init; } = string.Empty;
    public string Tone { get; init; } = "neutral";
    public string? IconText { get; init; }
    public string? Title { get; init; }
}
