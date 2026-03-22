using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;

namespace CoinBot.Infrastructure.Mfa;

public sealed class EmailOtpService : IEmailOtpService
{
    private readonly ApplicationDbContext dbContext;
    private readonly IDataProtector otpProtector;
    private readonly TimeProvider timeProvider;
    private readonly MfaOptions options;
    private readonly int upperBoundExclusive;

    public EmailOtpService(
        ApplicationDbContext dbContext,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider,
        IOptions<MfaOptions> options)
    {
        this.dbContext = dbContext;
        otpProtector = dataProtectionProvider.CreateProtector("CoinBot.Mfa.EmailOtp.v1");
        this.timeProvider = timeProvider;
        this.options = options.Value;
        upperBoundExclusive = CalculateExclusiveUpperBound(this.options.EmailOtpCodeLength);
    }

    public async Task<EmailOtpIssueResult> IssueAsync(string userId, string purpose, CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeUserId(userId);
        var normalizedPurpose = NormalizePurpose(purpose);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var otpCode = RandomNumberGenerator
            .GetInt32(0, upperBoundExclusive)
            .ToString($"D{options.EmailOtpCodeLength}");

        var challenge = new MfaEmailOtpChallenge
        {
            UserId = normalizedUserId,
            Purpose = normalizedPurpose,
            TokenCiphertext = otpProtector.Protect(otpCode),
            ExpiresAtUtc = utcNow.AddMinutes(options.EmailOtpLifetimeMinutes)
        };

        dbContext.MfaEmailOtpChallenges.Add(challenge);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new EmailOtpIssueResult(challenge.Id, otpCode, challenge.ExpiresAtUtc);
    }

    public async Task<bool> VerifyAsync(string userId, string purpose, string code, CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeUserId(userId);
        var normalizedPurpose = NormalizePurpose(purpose);
        var normalizedCode = NormalizeCode(code);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        var challenge = await dbContext.MfaEmailOtpChallenges
            .OrderByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(
                entity =>
                    entity.UserId == normalizedUserId &&
                    entity.Purpose == normalizedPurpose &&
                    entity.ConsumedAtUtc == null,
                cancellationToken);

        if (challenge is null)
        {
            return false;
        }

        if (challenge.ExpiresAtUtc <= utcNow || challenge.FailedAttemptCount >= options.EmailOtpMaxFailedAttempts)
        {
            return false;
        }

        if (!TryUnprotect(challenge.TokenCiphertext, out var expectedCode))
        {
            return false;
        }

        challenge.LastAttemptedAtUtc = utcNow;

        if (!FixedTimeEquals(normalizedCode, expectedCode))
        {
            challenge.FailedAttemptCount++;
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        challenge.ConsumedAtUtc = utcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private bool TryUnprotect(string protectedCode, out string code)
    {
        code = string.Empty;

        try
        {
            code = otpProtector.Unprotect(protectedCode);
            return !string.IsNullOrWhiteSpace(code);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string NormalizeUserId(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return userId.Trim();
    }

    private static string NormalizePurpose(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        var normalizedPurpose = purpose.Trim().ToLowerInvariant();

        if (normalizedPurpose.Length > 64)
        {
            throw new ArgumentException("MFA purpose cannot be longer than 64 characters.", nameof(purpose));
        }

        foreach (var currentChar in normalizedPurpose)
        {
            if (char.IsLetterOrDigit(currentChar) || currentChar is '.' or '_' or '-' or ':')
            {
                continue;
            }

            throw new ArgumentException("MFA purpose contains unsupported characters.", nameof(purpose));
        }

        return normalizedPurpose;
    }

    private static string NormalizeCode(string code)
    {
        return new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
    }

    private static int CalculateExclusiveUpperBound(int digits)
    {
        var upperBound = 1;

        for (var index = 0; index < digits; index++)
        {
            upperBound *= 10;
        }

        return upperBound;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
