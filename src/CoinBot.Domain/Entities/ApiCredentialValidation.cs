namespace CoinBot.Domain.Entities;

public sealed class ApiCredentialValidation : BaseEntity
{
    public Guid ApiCredentialId { get; set; }

    public Guid ExchangeAccountId { get; set; }

    public string OwnerUserId { get; set; } = string.Empty;

    public bool IsKeyValid { get; set; }

    public bool CanTrade { get; set; }

    public bool CanWithdraw { get; set; }

    public bool SupportsSpot { get; set; }

    public bool SupportsFutures { get; set; }

    public string? EnvironmentScope { get; set; }

    public bool IsEnvironmentMatch { get; set; }

    public bool HasTimestampSkew { get; set; }

    public bool HasIpRestrictionIssue { get; set; }

    public string ValidationStatus { get; set; } = string.Empty;

    public string PermissionSummary { get; set; } = string.Empty;

    public string? FailureReason { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime ValidatedAtUtc { get; set; }
}
