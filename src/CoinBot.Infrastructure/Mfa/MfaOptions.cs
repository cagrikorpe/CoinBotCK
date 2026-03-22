using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Mfa;

public sealed class MfaOptions
{
    [Range(4, 8)]
    public int EmailOtpCodeLength { get; init; } = 6;

    [Range(1, 30)]
    public int EmailOtpLifetimeMinutes { get; init; } = 10;

    [Range(1, 10)]
    public int EmailOtpMaxFailedAttempts { get; init; } = 5;

    [Range(6, 8)]
    public int TotpCodeLength { get; init; } = 6;

    [Range(15, 120)]
    public int TotpTimeStepSeconds { get; init; } = 30;

    [Range(0, 5)]
    public int TotpAllowedTimeDriftSteps { get; init; } = 1;

    [Range(10, 64)]
    public int TotpSecretSizeBytes { get; init; } = 20;
}
