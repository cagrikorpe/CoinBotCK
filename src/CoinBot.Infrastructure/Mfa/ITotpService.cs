namespace CoinBot.Infrastructure.Mfa;

public interface ITotpService
{
    string GenerateSecret();

    string ProtectSecret(string secret);

    bool VerifyCode(string? protectedSecret, string code);
}
