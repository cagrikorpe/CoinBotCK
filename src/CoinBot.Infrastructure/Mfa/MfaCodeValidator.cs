using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Identity;

namespace CoinBot.Infrastructure.Mfa;

public sealed class MfaCodeValidator : IMfaCodeValidator
{
    private readonly ITotpService totpService;
    private readonly IEmailOtpService emailOtpService;

    public MfaCodeValidator(ITotpService totpService, IEmailOtpService emailOtpService)
    {
        this.totpService = totpService;
        this.emailOtpService = emailOtpService;
    }

    public async Task<bool> ValidateAsync(
        ApplicationUser user,
        string provider,
        string code,
        string? purpose = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!user.MfaEnabled)
        {
            return false;
        }

        return NormalizeProvider(provider) switch
        {
            MfaProviders.AuthenticatorApp => user.TotpEnabled &&
                totpService.VerifyCode(user.TotpSecretCiphertext, code),

            MfaProviders.EmailOtp => user.EmailOtpEnabled &&
                await ValidateEmailOtpAsync(user.Id, purpose, code, cancellationToken),

            _ => false
        };
    }

    private async Task<bool> ValidateEmailOtpAsync(
        string userId,
        string? purpose,
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            return false;
        }

        try
        {
            return await emailOtpService.VerifyAsync(userId, purpose, code, cancellationToken);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeProvider(string provider)
    {
        return provider?.Trim().ToLowerInvariant() ?? string.Empty;
    }
}
