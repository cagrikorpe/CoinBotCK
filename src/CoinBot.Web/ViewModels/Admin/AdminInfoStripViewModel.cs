namespace CoinBot.Web.ViewModels.Admin;

public sealed class AdminInfoStripViewModel
{
    public string Tone { get; init; } = "info";
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? Meta { get; init; }
}
