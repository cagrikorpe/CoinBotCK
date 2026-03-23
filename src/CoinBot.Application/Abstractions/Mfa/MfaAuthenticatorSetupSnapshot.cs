namespace CoinBot.Application.Abstractions.Mfa;

public sealed record MfaAuthenticatorSetupSnapshot(
    string SharedKey,
    string DisplaySharedKey,
    string AuthenticatorUri);
