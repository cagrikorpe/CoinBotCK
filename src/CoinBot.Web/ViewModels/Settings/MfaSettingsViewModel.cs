namespace CoinBot.Web.ViewModels.Settings;

public sealed class MfaSettingsViewModel
{
    public bool IsMfaEnabled { get; init; }

    public bool IsTotpEnabled { get; init; }

    public bool IsEmailOtpEnabled { get; init; }

    public string? PreferredProvider { get; init; }

    public bool HasPendingAuthenticatorEnrollment { get; init; }

    public int ActiveRecoveryCodeCount { get; init; }

    public DateTime? UpdatedAtUtc { get; init; }

    public string? SharedKey { get; init; }

    public string? DisplaySharedKey { get; init; }

    public string? AuthenticatorUri { get; init; }

    public IReadOnlyList<string> RecoveryCodes { get; init; } = [];
}
