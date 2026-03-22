using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Credentials;

internal sealed class CredentialKeyResolver(
    IConfiguration configuration,
    IOptions<CredentialSecurityOptions> options) : ICredentialKeyResolver
{
    private readonly CredentialSecurityOptions optionsValue = options.Value;

    public string CurrentKeyVersion => optionsValue.KeyVersion;

    public byte[] ResolveKeyMaterial()
    {
        var encodedKeyMaterial = optionsValue.Provider switch
        {
            CredentialSecurityKeyProvider.Environment => Environment.GetEnvironmentVariable(optionsValue.EnvironmentVariableName),
            CredentialSecurityKeyProvider.Vault => configuration[optionsValue.VaultKeyConfigurationPath],
            _ => null
        };

        if (string.IsNullOrWhiteSpace(encodedKeyMaterial))
        {
            throw new InvalidOperationException(
                $"Credential encryption key material is not available for provider '{optionsValue.Provider}'.");
        }

        byte[] keyBytes;

        try
        {
            keyBytes = Convert.FromBase64String(encodedKeyMaterial.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Credential encryption key material must be a base64-encoded 256-bit key.", exception);
        }

        if (keyBytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            throw new InvalidOperationException("Credential encryption key material must decode to exactly 32 bytes for AES-256.");
        }

        return keyBytes;
    }
}
