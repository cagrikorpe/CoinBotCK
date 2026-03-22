namespace CoinBot.Infrastructure.Mfa;

public interface IEmailOtpService
{
    Task<EmailOtpIssueResult> IssueAsync(string userId, string purpose, CancellationToken cancellationToken = default);

    Task<bool> VerifyAsync(string userId, string purpose, string code, CancellationToken cancellationToken = default);
}
