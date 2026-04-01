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
        var encodedKeyMaterial = ResolveEncodedKeyMaterial();

        if (string.IsNullOrWhiteSpace(encodedKeyMaterial))
        {
            throw new CredentialSecurityConfigurationException(BuildMissingKeyMaterialMessage());
        }

        byte[] keyBytes;

        try
        {
            keyBytes = Convert.FromBase64String(encodedKeyMaterial.Trim());
        }
        catch (FormatException exception)
        {
            throw new CredentialSecurityConfigurationException(BuildInvalidKeyMaterialMessage("Credential encryption key material must be a base64-encoded 256-bit key."), exception);
        }

        if (keyBytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            throw new CredentialSecurityConfigurationException(BuildInvalidKeyMaterialMessage("Credential encryption key material must decode to exactly 32 bytes for AES-256."));
        }

        return keyBytes;
    }

    private string? ResolveEncodedKeyMaterial()
    {
        return optionsValue.Provider switch
        {
            CredentialSecurityKeyProvider.Environment => ResolveEnvironmentKeyMaterial(),
            CredentialSecurityKeyProvider.Vault => configuration[optionsValue.VaultKeyConfigurationPath],
            _ => null
        };
    }

    private string? ResolveEnvironmentKeyMaterial()
    {
        var environmentValue = Environment.GetEnvironmentVariable(optionsValue.EnvironmentVariableName);

        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        if (!IsDevelopmentEnvironment())
        {
            return null;
        }

        var developmentSecretValue = configuration[optionsValue.VaultKeyConfigurationPath];

        if (!string.IsNullOrWhiteSpace(developmentSecretValue))
        {
            return developmentSecretValue;
        }

        return configuration[optionsValue.EnvironmentVariableName];
    }

    private string BuildMissingKeyMaterialMessage()
    {
        return optionsValue.Provider switch
        {
            CredentialSecurityKeyProvider.Environment when IsDevelopmentEnvironment() =>
                $"Credential encryption key material is not available for provider '{optionsValue.Provider}'. Configure environment variable '{optionsValue.EnvironmentVariableName}' or development user-secrets/config key '{optionsValue.VaultKeyConfigurationPath}'.",
            CredentialSecurityKeyProvider.Environment =>
                $"Credential encryption key material is not available for provider '{optionsValue.Provider}'. Configure environment variable '{optionsValue.EnvironmentVariableName}'.",
            CredentialSecurityKeyProvider.Vault =>
                $"Credential encryption key material is not available for provider '{optionsValue.Provider}'. Configure '{optionsValue.VaultKeyConfigurationPath}'.",
            _ =>
                $"Credential encryption key material is not available for provider '{optionsValue.Provider}'."
        };
    }

    private string BuildInvalidKeyMaterialMessage(string message)
    {
        return optionsValue.Provider switch
        {
            CredentialSecurityKeyProvider.Environment when IsDevelopmentEnvironment() =>
                $"{message} Expected source: '{optionsValue.EnvironmentVariableName}' or development user-secrets/config key '{optionsValue.VaultKeyConfigurationPath}'.",
            CredentialSecurityKeyProvider.Environment =>
                $"{message} Expected source: '{optionsValue.EnvironmentVariableName}'.",
            CredentialSecurityKeyProvider.Vault =>
                $"{message} Expected source: '{optionsValue.VaultKeyConfigurationPath}'.",
            _ => message
        };
    }

    private static bool IsDevelopmentEnvironment()
    {
        return string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
    }
}
