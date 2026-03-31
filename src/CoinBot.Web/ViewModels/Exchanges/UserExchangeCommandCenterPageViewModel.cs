using CoinBot.Application.Abstractions.ExchangeCredentials;

namespace CoinBot.Web.ViewModels.Exchanges;

public sealed class UserExchangeCommandCenterPageViewModel
{
    public required UserExchangeCommandCenterSnapshot Snapshot { get; init; }

    public required BinanceCredentialConnectInputModel Form { get; init; }

    public string? SuccessMessage { get; init; }

    public string? ErrorMessage { get; init; }

    public string SubmitAction { get; init; } = string.Empty;

    public string SubmitController { get; init; } = string.Empty;

    public string SubmitArea { get; init; } = string.Empty;

    public bool ShowOnboardingFooterActions { get; init; }
}
