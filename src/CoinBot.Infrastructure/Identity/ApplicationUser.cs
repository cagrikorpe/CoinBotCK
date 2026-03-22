using CoinBot.Domain.Enums;
using Microsoft.AspNetCore.Identity;

namespace CoinBot.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;

    public bool MfaEnabled { get; set; }

    public bool TotpEnabled { get; set; }

    public bool EmailOtpEnabled { get; set; }

    public string? PreferredMfaProvider { get; set; }

    public string? TotpSecretCiphertext { get; set; }

    public DateTime? MfaUpdatedAtUtc { get; set; }

    public ExecutionEnvironment? TradingModeOverride { get; set; }

    public DateTime? TradingModeApprovedAtUtc { get; set; }

    public string? TradingModeApprovalReference { get; set; }
}
