namespace CoinBot.Application.Abstractions.Mfa;

public sealed record MfaStatusSnapshot(
    bool IsMfaEnabled,
    bool IsTotpEnabled,
    bool IsEmailOtpEnabled,
    string? PreferredProvider,
    bool HasPendingAuthenticatorEnrollment,
    int ActiveRecoveryCodeCount,
    DateTime? UpdatedAtUtc);
