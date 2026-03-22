using System.Xml.Linq;
using CoinBot.Application.Abstractions.DataScope;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace CoinBot.Infrastructure.Persistence;

public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var basePath = ResolveConfigurationBasePath();
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";

        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environmentName}.json", optional: true);

        var userSecretsPath = ResolveUserSecretsPath(basePath);

        if (!string.IsNullOrWhiteSpace(userSecretsPath))
        {
            configurationBuilder.AddJsonFile(userSecretsPath, optional: true);
        }

        var configuration = configurationBuilder
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
        }

        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
            sqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName));

        return new ApplicationDbContext(optionsBuilder.Options, new DesignTimeDataScopeContext());
    }

    private static string ResolveConfigurationBasePath()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            Path.Combine(currentDirectory, "..", "CoinBot.Web"),
            Path.Combine(currentDirectory, "src", "CoinBot.Web"),
            currentDirectory
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);

            if (File.Exists(Path.Combine(fullPath, "appsettings.json")))
            {
                return fullPath;
            }
        }

        return currentDirectory;
    }

    private static string? ResolveUserSecretsPath(string basePath)
    {
        var projectFilePath = Path.Combine(basePath, "CoinBot.Web.csproj");

        if (!File.Exists(projectFilePath))
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(projectFilePath);

            var userSecretsId = document.Root?
                .Descendants()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, "UserSecretsId", StringComparison.Ordinal))?
                .Value
                .Trim();

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
        catch
        {
            return null;
        }
    }

    private sealed class DesignTimeDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => false;
    }
}
