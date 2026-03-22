namespace CoinBot.Web.ViewModels.Admin;

public sealed class AdminTableScaffoldViewModel
{
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();
    public int SkeletonRowCount { get; init; } = 4;
}
