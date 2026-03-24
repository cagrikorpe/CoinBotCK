namespace CoinBot.Application.Abstractions.Policy;

public sealed record GlobalPolicyRollbackRequest(
    int TargetVersion,
    string ActorUserId,
    string Reason,
    string? CorrelationId = null,
    string? Source = null,
    string? IpAddress = null,
    string? UserAgent = null);
