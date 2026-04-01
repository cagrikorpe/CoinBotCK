namespace CoinBot.Infrastructure.Credentials;

public sealed class CredentialSecurityConfigurationException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
