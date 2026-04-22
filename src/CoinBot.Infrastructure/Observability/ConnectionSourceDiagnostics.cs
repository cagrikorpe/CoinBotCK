using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace CoinBot.Infrastructure.Observability;

public static class ConnectionSourceDiagnostics
{
    public static ConnectionSourceAudit Describe(
        IConfiguration configuration,
        string environmentName,
        string contentRootPath,
        IReadOnlyList<string>? args,
        string connectionStringName = "DefaultConnection",
        string? userSecretsFilePath = null)
    {
        var effectiveConnectionString = configuration.GetConnectionString(connectionStringName);
        var commandLineValue = TryResolveCommandLineOverride(args, connectionStringName);
        var environmentOverrideKey = $"ConnectionStrings__{connectionStringName}";
        var environmentValue = Environment.GetEnvironmentVariable(environmentOverrideKey);
        var secretsValue = TryReadConnectionStringValue(
            userSecretsFilePath ?? ResolveUserSecretsFilePath(),
            connectionStringName,
            allowFlatKeys: true);
        var developmentSettingsValue = TryReadConnectionStringValue(
            Path.Combine(contentRootPath, $"appsettings.{environmentName}.json"),
            connectionStringName,
            allowFlatKeys: false);
        var baseSettingsValue = TryReadConnectionStringValue(
            Path.Combine(contentRootPath, "appsettings.json"),
            connectionStringName,
            allowFlatKeys: false);

        var sourceCategory = ResolveSourceCategory(
            effectiveConnectionString,
            commandLineValue,
            environmentValue,
            secretsValue,
            developmentSettingsValue,
            baseSettingsValue);

        return CreateAudit(
            sourceCategory,
            effectiveConnectionString,
            environmentOverridePresent: !string.IsNullOrWhiteSpace(environmentValue));
    }

    private static ConnectionSourceAudit CreateAudit(
        string sourceCategory,
        string? connectionString,
        bool environmentOverridePresent)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ConnectionSourceAudit(
                sourceCategory,
                "unknown",
                "unknown",
                "Unknown",
                false,
                false,
                environmentOverridePresent);
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var authMode = builder.IntegratedSecurity
                ? "IntegratedSecurity"
                : (!string.IsNullOrWhiteSpace(builder.UserID) || !string.IsNullOrWhiteSpace(builder.Password))
                    ? "SqlPassword"
                    : "Unknown";

            return new ConnectionSourceAudit(
                sourceCategory,
                string.IsNullOrWhiteSpace(builder.DataSource) ? "unknown" : builder.DataSource,
                string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "unknown" : builder.InitialCatalog,
                authMode,
                !string.IsNullOrWhiteSpace(builder.UserID),
                !string.IsNullOrWhiteSpace(builder.Password),
                environmentOverridePresent);
        }
        catch (ArgumentException)
        {
            return new ConnectionSourceAudit(
                sourceCategory,
                "unparsed",
                "unparsed",
                "Unknown",
                false,
                false,
                environmentOverridePresent);
        }
    }

    private static string ResolveSourceCategory(
        string? effectiveConnectionString,
        string? commandLineValue,
        string? environmentValue,
        string? userSecretsValue,
        string? developmentSettingsValue,
        string? baseSettingsValue)
    {
        if (HasMatchingValue(effectiveConnectionString, commandLineValue))
        {
            return "CommandLine";
        }

        if (HasMatchingValue(effectiveConnectionString, environmentValue))
        {
            return "EnvironmentVariable";
        }

        if (HasMatchingValue(effectiveConnectionString, userSecretsValue))
        {
            return "UserSecrets";
        }

        if (HasMatchingValue(effectiveConnectionString, developmentSettingsValue))
        {
            return "AppSettingsDevelopment";
        }

        if (HasMatchingValue(effectiveConnectionString, baseSettingsValue))
        {
            return "AppSettingsBase";
        }

        return "UnknownFallback";
    }

    private static bool HasMatchingValue(string? effectiveConnectionString, string? candidateValue)
    {
        return !string.IsNullOrWhiteSpace(effectiveConnectionString) &&
               !string.IsNullOrWhiteSpace(candidateValue) &&
               string.Equals(effectiveConnectionString, candidateValue, StringComparison.Ordinal);
    }

    private static string? TryResolveCommandLineOverride(IReadOnlyList<string>? args, string connectionStringName)
    {
        if (args is null || args.Count == 0)
        {
            return null;
        }

        var acceptedKeys = new[]
        {
            $"ConnectionStrings:{connectionStringName}",
            $"ConnectionStrings__{connectionStringName}"
        };

        for (var index = 0; index < args.Count; index++)
        {
            var rawArgument = args[index];

            if (string.IsNullOrWhiteSpace(rawArgument))
            {
                continue;
            }

            var normalizedArgument = rawArgument.Trim();
            normalizedArgument = normalizedArgument.TrimStart('-');
            normalizedArgument = normalizedArgument.TrimStart('/');

            var separatorIndex = normalizedArgument.IndexOf('=');

            if (separatorIndex >= 0)
            {
                var key = normalizedArgument[..separatorIndex];

                if (acceptedKeys.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)))
                {
                    return normalizedArgument[(separatorIndex + 1)..];
                }

                continue;
            }

            if (!acceptedKeys.Any(candidate => string.Equals(candidate, normalizedArgument, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (index + 1 >= args.Count)
            {
                return string.Empty;
            }

            return args[index + 1];
        }

        return null;
    }

    private static string? ResolveUserSecretsFilePath()
    {
        var entryAssembly = Assembly.GetEntryAssembly();

        if (entryAssembly is null)
        {
            return null;
        }

        var userSecretsId = entryAssembly
            .GetCustomAttributesData()
            .FirstOrDefault(attribute =>
                string.Equals(
                    attribute.AttributeType.FullName,
                    "Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute",
                    StringComparison.Ordinal))
            ?.ConstructorArguments
            .FirstOrDefault()
            .Value as string;

        if (string.IsNullOrWhiteSpace(userSecretsId))
        {
            return null;
        }

        var applicationDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (string.IsNullOrWhiteSpace(applicationDataPath))
        {
            return null;
        }

        return Path.Combine(applicationDataPath, "Microsoft", "UserSecrets", userSecretsId, "secrets.json");
    }

    private static string? TryReadConnectionStringValue(
        string? filePath,
        string connectionStringName,
        bool allowFlatKeys)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = document.RootElement;

            if (allowFlatKeys &&
                root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty($"ConnectionStrings:{connectionStringName}", out var flatValue) &&
                flatValue.ValueKind == JsonValueKind.String)
            {
                return flatValue.GetString();
            }

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("ConnectionStrings", out var connectionStrings) ||
                connectionStrings.ValueKind != JsonValueKind.Object ||
                !connectionStrings.TryGetProperty(connectionStringName, out var nestedValue) ||
                nestedValue.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return nestedValue.GetString();
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record ConnectionSourceAudit(
    string SourceCategory,
    string DbServer,
    string DbName,
    string AuthMode,
    bool UserPresent,
    bool PasswordPresent,
    bool EnvironmentOverridePresent);
