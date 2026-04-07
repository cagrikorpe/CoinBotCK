using System.Security.Claims;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Mfa;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using CoinBot.IntegrationTests.Infrastructure;
using CoinBot.Web.Controllers;
using CoinBot.Web.ViewModels.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;

namespace CoinBot.IntegrationTests.Settings;

public sealed class SettingsPersistenceIntegrationTests
{
    [Fact]
    [Trait("Scope", "Settings")]
    public async Task EnsureIdentitySeedDataAsync_AppliesPreferredTimeZoneMigration_ToRealSqlServer()
    {
        var databaseName = $"CoinBot_SettingsSeed_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        await using var provider = BuildInfrastructureProvider(connectionString, Environments.Production);

        try
        {
            await provider.EnsureIdentitySeedDataAsync(provider.GetRequiredService<IConfiguration>());

            await using var scope = provider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var historyRows = await dbContext.Database
                .SqlQueryRaw<string>("SELECT [MigrationId] AS [Value] FROM [__EFMigrationsHistory]")
                .ToListAsync();
            var hasColumn = await dbContext.Database
                .SqlQueryRaw<int>(
                    """
                    SELECT COUNT(*) AS [Value]
                    FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_NAME = 'AspNetUsers' AND COLUMN_NAME = 'PreferredTimeZoneId'
                    """)
                .SingleAsync();

            Assert.Contains(historyRows, row => string.Equals(row, "20260402091430_AddUserTimeZonePreference", StringComparison.Ordinal));
            Assert.Equal(1, hasColumn);
        }
        finally
        {
            await CleanupDatabaseAsync(connectionString);
        }
    }

    [Fact]
    [Trait("Scope", "Settings")]
    public async Task SettingsController_SaveAndReadBackPreferredTimeZone_UsesRealSqlServer()
    {
        var databaseName = $"CoinBot_SettingsUi_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        await using var provider = BuildInfrastructureProvider(connectionString, Environments.Production);
        var userId = Guid.NewGuid().ToString("N");
        var initialTimeZoneId = "UTC";
        var updatedTimeZoneId = ResolveNonUtcTimeZoneId();

        try
        {
            await provider.EnsureIdentitySeedDataAsync(provider.GetRequiredService<IConfiguration>());
            await SeedUserAsync(provider, userId, initialTimeZoneId);

            var firstGetController = await CreateControllerAsync(provider, userId, "trace-settings-get-01");
            var firstGetResult = await firstGetController.Index(CancellationToken.None);
            var firstGetModel = Assert.IsType<SettingsIndexViewModel>(Assert.IsType<ViewResult>(firstGetResult).Model);
            Assert.Equal(initialTimeZoneId, firstGetModel.Form.PreferredTimeZoneId);

            var postController = await CreateControllerAsync(provider, userId, "trace-settings-post-01");
            var postResult = await postController.Index(
                new TimeZoneSettingsInputModel { PreferredTimeZoneId = updatedTimeZoneId },
                CancellationToken.None);

            Assert.IsType<RedirectToActionResult>(postResult);

            await using (var verifyScope = provider.CreateAsyncScope())
            {
                var dbContext = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var user = await dbContext.Users.SingleAsync(entity => entity.Id == userId);
                Assert.Equal(updatedTimeZoneId, user.PreferredTimeZoneId);
            }

            var secondGetController = await CreateControllerAsync(provider, userId, "trace-settings-get-02");
            var secondGetResult = await secondGetController.Index(CancellationToken.None);
            var secondGetModel = Assert.IsType<SettingsIndexViewModel>(Assert.IsType<ViewResult>(secondGetResult).Model);
            Assert.Equal(updatedTimeZoneId, secondGetModel.Form.PreferredTimeZoneId);
        }
        finally
        {
            await CleanupDatabaseAsync(connectionString);
        }
    }

    private static async Task<SettingsController> CreateControllerAsync(IServiceProvider provider, string userId, string traceIdentifier)
    {
        var scope = provider.CreateAsyncScope();
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = traceIdentifier,
            RequestServices = scope.ServiceProvider
        };
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId)],
                "IntegrationAuth"));

        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;
        var controller = new SettingsController(
            new FakeMfaManagementService(),
            scope.ServiceProvider.GetRequiredService<CoinBot.Application.Abstractions.Settings.IUserSettingsService>());

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        controller.Url = new TestUrlHelper();
        httpContext.Response.RegisterForDisposeAsync(scope);
        return controller;
    }

    private static async Task SeedUserAsync(IServiceProvider provider, string userId, string preferredTimeZoneId)
    {
        await using var scope = provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = $"settings.{userId}@coinbot.test",
            Email = $"settings.{userId}@coinbot.test",
            FullName = "Settings Integration User",
            EmailConfirmed = true,
            PreferredTimeZoneId = preferredTimeZoneId
        };

        var result = await userManager.CreateAsync(user, "Passw0rd!");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
    }

    private static async Task CleanupDatabaseAsync(string connectionString)
    {
        await SqlServerIntegrationDatabase.CleanupDatabaseAsync(connectionString);
    }

    private static ServiceProvider BuildInfrastructureProvider(string connectionString, string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["IdentitySeed:SuperAdminEmail"] = null,
                ["IdentitySeed:SuperAdminPassword"] = null,
                ["IdentitySeed:SuperAdminFullName"] = null
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));
        services.AddInfrastructure(configuration);
        services.AddSingleton<IBinanceTimeSyncService>(new FakeTimeSyncService());
        return services.BuildServiceProvider();
    }

    private static DbContextOptions<ApplicationDbContext> CreateOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
    }

    private static string ResolveConnectionString(string databaseName)
    {
        return SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
    }

    private static string ResolveNonUtcTimeZoneId()
    {
        return TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => zone.Id)
            .FirstOrDefault(id => !string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase))
            ?? "UTC";
    }

    private sealed class FakeMfaManagementService : IMfaManagementService
    {
        public Task<CoinBot.Application.Abstractions.Mfa.MfaStatusSnapshot> GetStatusAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CoinBot.Application.Abstractions.Mfa.MfaAuthenticatorSetupSnapshot?> GetAuthenticatorSetupAsync(string userId, bool createIfMissing = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> EnableAuthenticatorAsync(string userId, string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DisableAsync(string userId, string verificationCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(string userId, string verificationCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> VerifyAsync(string userId, string provider, string code, string? purpose = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryRedeemRecoveryCodeAsync(string userId, string recoveryCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);

        public IDictionary<string, object> LoadTempData(HttpContext context)
        {
            return new Dictionary<string, object>(values, StringComparer.Ordinal);
        }

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            this.values.Clear();

            foreach (var pair in values)
            {
                this.values[pair.Key] = pair.Value;
            }
        }
    }

    private sealed class TestUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();

        public string? Action(UrlActionContext actionContext) => "/settings";

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => true;

        public string? Link(string? routeName, object? values) => "/settings";

        public string? RouteUrl(UrlRouteContext routeContext) => "/settings";
    }

    private sealed class TestDataScopeContext : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => false;
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "CoinBot.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeTimeSyncService : IBinanceTimeSyncService
    {
        public Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new BinanceTimeSyncSnapshot(
                new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                0,
                10,
                new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                forceRefresh ? "Synchronized" : "Cached",
                null));
        }

        public Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1_710_000_000_000L);
        }
    }
}


