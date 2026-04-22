using System.Text.Json;
using CoinBot.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;

namespace CoinBot.UnitTests.Infrastructure.Observability;

public sealed class ConnectionSourceDiagnosticsTests
{
    [Fact]
    public void Describe_ReturnsEnvironmentVariable_WhenEnvironmentOverrideMatchesEffectiveConnection()
    {
        const string envVariableName = "ConnectionStrings__DefaultConnection";
        var originalValue = Environment.GetEnvironmentVariable(envVariableName);
        var contentRoot = CreateContentRoot();
        var secretsPath = CreateSecretsFile("Server=secrets;Database=CoinBotDb;User Id=secret-user;Password=secret;TrustServerCertificate=True");
        const string environmentValue = "Server=env-host;Database=CoinBotDb;User Id=LocalCoinbot;Password=masked;TrustServerCertificate=True";

        try
        {
            Environment.SetEnvironmentVariable(envVariableName, environmentValue);
            WriteAppSettings(contentRoot, "appsettings.json", "Server=base;Database=CoinBotDb;Trusted_Connection=True");
            WriteAppSettings(contentRoot, "appsettings.Development.json", "Server=dev;Database=CoinBotDb;Trusted_Connection=True");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = environmentValue
                })
                .Build();

            var audit = ConnectionSourceDiagnostics.Describe(
                configuration,
                "Development",
                contentRoot,
                [],
                userSecretsFilePath: secretsPath);

            Assert.Equal("EnvironmentVariable", audit.SourceCategory);
            Assert.Equal("env-host", audit.DbServer);
            Assert.Equal("CoinBotDb", audit.DbName);
            Assert.Equal("SqlPassword", audit.AuthMode);
            Assert.True(audit.UserPresent);
            Assert.True(audit.PasswordPresent);
            Assert.True(audit.EnvironmentOverridePresent);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVariableName, originalValue);
            DeletePath(contentRoot);
            DeletePath(Path.GetDirectoryName(secretsPath)!);
        }
    }

    [Fact]
    public void Describe_ReturnsUserSecrets_WhenNoHigherPrioritySourceExists()
    {
        var contentRoot = CreateContentRoot();
        var secretsValue = "Server=localhost;Database=CoinBotDb;User Id=LocalCoinbot;Password=masked;TrustServerCertificate=True";
        var secretsPath = CreateSecretsFile(secretsValue);

        try
        {
            WriteAppSettings(contentRoot, "appsettings.json", "Server=base;Database=CoinBotDb;Trusted_Connection=True");
            WriteAppSettings(contentRoot, "appsettings.Development.json", "Server=dev;Database=CoinBotDb;Trusted_Connection=True");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = secretsValue
                })
                .Build();

            var audit = ConnectionSourceDiagnostics.Describe(
                configuration,
                "Development",
                contentRoot,
                [],
                userSecretsFilePath: secretsPath);

            Assert.Equal("UserSecrets", audit.SourceCategory);
            Assert.Equal("localhost", audit.DbServer);
            Assert.Equal("SqlPassword", audit.AuthMode);
            Assert.True(audit.UserPresent);
            Assert.True(audit.PasswordPresent);
            Assert.False(audit.EnvironmentOverridePresent);
        }
        finally
        {
            DeletePath(contentRoot);
            DeletePath(Path.GetDirectoryName(secretsPath)!);
        }
    }

    [Fact]
    public void Describe_ReturnsAppSettingsDevelopment_WhenNoHigherPrioritySourceExists()
    {
        var contentRoot = CreateContentRoot();
        const string developmentValue = "Server=dev-host;Database=CoinBotDb;Trusted_Connection=True";

        try
        {
            WriteAppSettings(contentRoot, "appsettings.json", "Server=base-host;Database=CoinBotDb;Trusted_Connection=True");
            WriteAppSettings(contentRoot, "appsettings.Development.json", developmentValue);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = developmentValue
                })
                .Build();

            var audit = ConnectionSourceDiagnostics.Describe(
                configuration,
                "Development",
                contentRoot,
                [],
                userSecretsFilePath: null);

            Assert.Equal("AppSettingsDevelopment", audit.SourceCategory);
            Assert.Equal("dev-host", audit.DbServer);
            Assert.Equal("IntegratedSecurity", audit.AuthMode);
            Assert.False(audit.UserPresent);
            Assert.False(audit.PasswordPresent);
        }
        finally
        {
            DeletePath(contentRoot);
        }
    }

    [Fact]
    public void Describe_ReturnsCommandLine_WhenCommandLineOverrideMatchesEffectiveConnection()
    {
        const string commandLineValue = "Server=cmd-host;Database=CoinBotDb;User Id=cli-user;Password=masked;TrustServerCertificate=True";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = commandLineValue
            })
            .Build();

        var audit = ConnectionSourceDiagnostics.Describe(
            configuration,
            "Development",
            Path.GetTempPath(),
            [$"--ConnectionStrings:DefaultConnection={commandLineValue}"],
            userSecretsFilePath: null);

        Assert.Equal("CommandLine", audit.SourceCategory);
        Assert.Equal("cmd-host", audit.DbServer);
        Assert.Equal("SqlPassword", audit.AuthMode);
        Assert.True(audit.UserPresent);
        Assert.True(audit.PasswordPresent);
    }

    private static string CreateContentRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "coinbot-conn-audit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateSecretsFile(string connectionString)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "coinbot-conn-secrets", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var filePath = Path.Combine(directoryPath, "secrets.json");
        var payload = JsonSerializer.Serialize(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString
            });
        File.WriteAllText(filePath, payload);
        return filePath;
    }

    private static void WriteAppSettings(string contentRoot, string fileName, string connectionString)
    {
        var payload = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["ConnectionStrings"] = new Dictionary<string, string?>
                {
                    ["DefaultConnection"] = connectionString
                }
            });
        File.WriteAllText(Path.Combine(contentRoot, fileName), payload);
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
