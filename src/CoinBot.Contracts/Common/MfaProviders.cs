namespace CoinBot.Contracts.Common;

public static class MfaProviders
{
    public const string AuthenticatorApp = "authenticator-app";
    public const string EmailOtp = "email-otp";

    public static readonly string[] All =
    [
        AuthenticatorApp,
        EmailOtp
    ];
}
