namespace CoinBot.Web.ViewModels.Foundation;

public sealed class UiStateViewModel
{
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string IconText { get; init; } = "i";
    public string? ActionText { get; init; }
    public string? ActionHref { get; init; }
    public string? SecondaryActionText { get; init; }
    public string? SecondaryActionHref { get; init; }
}
