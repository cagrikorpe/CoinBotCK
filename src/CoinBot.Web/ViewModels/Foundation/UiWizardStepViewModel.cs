namespace CoinBot.Web.ViewModels.Foundation;

public sealed class UiWizardStepViewModel
{
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
    public bool IsCurrent { get; init; }
    public bool IsCompleted { get; init; }
}
