using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Identity;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Persistence;

public static class IdentitySeedData
{
    public static async Task EnsureIdentitySeedDataAsync(this IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();

        var scopedServices = scope.ServiceProvider;
        var logger = scopedServices.GetRequiredService<ILoggerFactory>().CreateLogger("CoinBot.IdentitySeed");
        var dbContext = scopedServices.GetRequiredService<ApplicationDbContext>();
        var hostEnvironment = scopedServices.GetRequiredService<IHostEnvironment>();
        var roleManager = scopedServices.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scopedServices.GetRequiredService<UserManager<ApplicationUser>>();
        var connectionTarget = DescribeConnectionTarget(dbContext);

        logger.LogInformation(
            "Identity seed starting. Provider={Provider} DataSource={DataSource} Database={Database} IntegratedSecurity={IntegratedSecurity} Encrypt={Encrypt} TrustServerCertificate={TrustServerCertificate}",
            connectionTarget.Provider,
            connectionTarget.DataSource,
            connectionTarget.Database,
            connectionTarget.IntegratedSecurity,
            connectionTarget.Encrypt,
            connectionTarget.TrustServerCertificate);

        if (hostEnvironment.IsDevelopment() &&
            !await CanReachDatabaseAsync(dbContext, logger, cancellationToken))
        {
            logger.LogWarning(
                "Identity seed skipped in Development because the configured database is unreachable. DataSource={DataSource} Database={Database}",
                connectionTarget.DataSource,
                connectionTarget.Database);
            return;
        }

        await dbContext.Database.MigrateAsync(cancellationToken);
        await EnsureTradingBotDirectionModeColumnAsync(dbContext, logger, cancellationToken);

        foreach (var roleName in ApplicationRoles.All)
        {
            var role = await roleManager.FindByNameAsync(roleName);

            if (role is null)
            {
                role = new IdentityRole(roleName);
                var createRoleResult = await roleManager.CreateAsync(role);

                if (!createRoleResult.Succeeded)
                {
                    throw new InvalidOperationException($"Failed to create role '{roleName}'.");
                }
            }

            await EnsureRoleClaimsAsync(roleManager, role, roleName);
        }

        var superAdminEmail = configuration["IdentitySeed:SuperAdminEmail"];
        var superAdminPassword = configuration["IdentitySeed:SuperAdminPassword"];
        var superAdminFullName = configuration["IdentitySeed:SuperAdminFullName"];

        if (string.IsNullOrWhiteSpace(superAdminEmail) || string.IsNullOrWhiteSpace(superAdminPassword))
        {
            logger.LogInformation(
                "Identity roles seeded. DataSource={DataSource} Database={Database} RoleCount={RoleCount}. Super admin creation skipped because secure seed configuration was not provided.",
                connectionTarget.DataSource,
                connectionTarget.Database,
                await roleManager.Roles.CountAsync(cancellationToken));
            return;
        }

        var user = await userManager.FindByEmailAsync(superAdminEmail);

        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = superAdminEmail,
                Email = superAdminEmail,
                FullName = string.IsNullOrWhiteSpace(superAdminFullName) ? "Super Admin" : superAdminFullName,
                EmailConfirmed = true,
                LockoutEnabled = true,
                TwoFactorEnabled = false
            };

            var createUserResult = await userManager.CreateAsync(user, superAdminPassword);

            if (!createUserResult.Succeeded)
            {
                throw new InvalidOperationException("Failed to create the initial super admin user.");
            }
        }

        if (!await userManager.IsInRoleAsync(user, ApplicationRoles.SuperAdmin))
        {
            var addRoleResult = await userManager.AddToRoleAsync(user, ApplicationRoles.SuperAdmin);

            if (!addRoleResult.Succeeded)
            {
                throw new InvalidOperationException("Failed to assign the SuperAdmin role to the initial user.");
            }
        }

        logger.LogInformation(
            "Identity roles and initial super admin seed completed. DataSource={DataSource} Database={Database} RoleCount={RoleCount}.",
            connectionTarget.DataSource,
            connectionTarget.Database,
            await roleManager.Roles.CountAsync(cancellationToken));
    }

    private static async Task EnsureTradingBotDirectionModeColumnAsync(
        ApplicationDbContext dbContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
        {
            return;
        }

        var connection = dbContext.Database.GetDbConnection();
        var shouldCloseConnection = connection.State != ConnectionState.Open;

        if (shouldCloseConnection)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            using var existsCommand = connection.CreateCommand();
            existsCommand.CommandText = "SELECT COL_LENGTH('dbo.TradingBots', 'DirectionMode')";
            var existsResult = await existsCommand.ExecuteScalarAsync(cancellationToken);

            if (existsResult is not null && existsResult is not DBNull)
            {
                return;
            }

            logger.LogWarning(
                "TradingBots.DirectionMode column is missing. Applying startup schema self-heal to keep runtime aligned with the current model.");

            await dbContext.Database.ExecuteSqlRawAsync(
                """
                IF COL_LENGTH('dbo.TradingBots', 'DirectionMode') IS NULL
                BEGIN
                    ALTER TABLE [dbo].[TradingBots]
                    ADD [DirectionMode] nvarchar(16) NOT NULL
                        CONSTRAINT [DF_TradingBots_DirectionMode] DEFAULT N'LongOnly';
                END
                """,
                cancellationToken);

            logger.LogInformation(
                "TradingBots.DirectionMode startup schema self-heal completed successfully.");
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private static async Task EnsureRoleClaimsAsync(RoleManager<IdentityRole> roleManager, IdentityRole role, string roleName)
    {
        var existingClaims = await roleManager.GetClaimsAsync(role);

        foreach (var requiredClaim in ApplicationRoleClaims.GetClaims(roleName))
        {
            var claimAlreadyExists = existingClaims.Any(existingClaim =>
                existingClaim.Type == requiredClaim.Type &&
                existingClaim.Value == requiredClaim.Value);

            if (claimAlreadyExists)
            {
                continue;
            }

            var addClaimResult = await roleManager.AddClaimAsync(role, requiredClaim);

            if (!addClaimResult.Succeeded)
            {
                throw new InvalidOperationException($"Failed to assign claim '{requiredClaim.Value}' to role '{roleName}'.");
            }
        }
    }

    private static ConnectionTargetInfo DescribeConnectionTarget(ApplicationDbContext dbContext)
    {
        var provider = dbContext.Database.ProviderName ?? "unknown";
        var connectionString = dbContext.Database.GetConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return new ConnectionTargetInfo(provider, "unknown", "unknown", null, null, null);
        }

        try
        {
            var builder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            return new ConnectionTargetInfo(
                provider,
                GetValue(builder, "Data Source", "Server", "Addr", "Address", "Network Address"),
                GetValue(builder, "Initial Catalog", "Database"),
                GetBooleanValue(builder, "Integrated Security", "Trusted_Connection"),
                GetBooleanValue(builder, "Encrypt"),
                GetBooleanValue(builder, "TrustServerCertificate", "Trust Server Certificate"));
        }
        catch
        {
            return new ConnectionTargetInfo(provider, "unparsed", "unparsed", null, null, null);
        }
    }

    private static string GetValue(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (builder.TryGetValue(key, out var value) && value is not null)
            {
                var text = value.ToString();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return "unknown";
    }

    private static bool? GetBooleanValue(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!builder.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            if (value is bool booleanValue)
            {
                return booleanValue;
            }

            if (bool.TryParse(value.ToString(), out var parsedValue))
            {
                return parsedValue;
            }
        }

        return null;
    }

    private static async Task<bool> CanReachDatabaseAsync(
        ApplicationDbContext dbContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var connectionString = dbContext.Database.GetConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            if (string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal))
            {
                var sqlConnectionStringBuilder = new SqlConnectionStringBuilder(connectionString)
                {
                    ConnectTimeout = Math.Min(
                        Math.Max(new SqlConnectionStringBuilder(connectionString).ConnectTimeout, 1),
                        3),
                    Pooling = false
                };

                await using var sqlConnection = new SqlConnection(sqlConnectionStringBuilder.ConnectionString);
                using var sqlTimeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sqlTimeoutCancellation.CancelAfter(TimeSpan.FromSeconds(sqlConnectionStringBuilder.ConnectTimeout + 1));
                await sqlConnection.OpenAsync(sqlTimeoutCancellation.Token);
                await sqlConnection.CloseAsync();
                return true;
            }

            using var genericTimeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            genericTimeoutCancellation.CancelAfter(TimeSpan.FromSeconds(3));
            return await dbContext.Database.CanConnectAsync(genericTimeoutCancellation.Token);
        }
        catch (Exception exception) when (exception is SqlException or DbException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Identity seed database connectivity probe failed.");
            return false;
        }
    }

    private readonly record struct ConnectionTargetInfo(
        string Provider,
        string DataSource,
        string Database,
        bool? IntegratedSecurity,
        bool? Encrypt,
        bool? TrustServerCertificate);
}
