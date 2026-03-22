namespace CoinBot.Infrastructure.Credentials;

internal interface ICredentialKeyResolver
{
    string CurrentKeyVersion { get; }

    byte[] ResolveKeyMaterial();
}
