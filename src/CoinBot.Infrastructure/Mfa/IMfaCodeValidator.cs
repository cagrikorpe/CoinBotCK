using CoinBot.Infrastructure.Identity;

namespace CoinBot.Infrastructure.Mfa;

public interface IMfaCodeValidator
{
    Task<bool> ValidateAsync(
        ApplicationUser user,
        string provider,
        string code,
        string? purpose = null,
        CancellationToken cancellationToken = default);
}
