namespace CoinBot.Infrastructure.Mfa;

public sealed record EmailOtpIssueResult(Guid ChallengeId, string Code, DateTime ExpiresAtUtc);
