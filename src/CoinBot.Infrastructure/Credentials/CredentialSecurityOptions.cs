using System.ComponentModel.DataAnnotations;

namespace CoinBot.Infrastructure.Credentials;

public sealed class CredentialSecurityOptions
{
    public CredentialSecurityKeyProvider Provider { get; init; } = CredentialSecurityKeyProvider.Environment;

    [Required]
    [MaxLength(64)]
    public string KeyVersion { get; init; } = "credential-v1";

    [Required]
    [MaxLength(128)]
    public string EnvironmentVariableName { get; init; } = "COINBOT_CREDENTIAL_ENCRYPTION_KEY_BASE64";

    [Required]
    [MaxLength(256)]
    public string VaultKeyConfigurationPath { get; init; } = "CredentialSecurity:Vault:ResolvedKey";

    [Range(1, 365)]
    public int RevalidationIntervalDays { get; init; } = 30;

    [Range(1, 365)]
    public int RotationIntervalDays { get; init; } = 90;
}
