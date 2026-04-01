using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Credentials;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Monitoring;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Administration;

public sealed class UserWorkspaceCredentialIntegrationTests
{
    [Fact]
    public async Task UserExchangeCommandCenterService_PersistsValidationHistory_AndFailsClosedOnWithdrawPermission()
    {
        var databaseName = $"CoinBotUserExchangeInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var options = CreateOptions(connectionString);
        var environmentVariableName = $"COINBOT_CREDENTIAL_INT_{Guid.NewGuid():N}";
        var previousEnvironmentValue = Environment.GetEnvironmentVariable(environmentVariableName);

        Environment.SetEnvironmentVariable(environmentVariableName, PrimaryKeyBase64);

        try
        {
            await using (var setupContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true)))
            {
                await setupContext.Database.EnsureDeletedAsync();
                await setupContext.Database.EnsureCreatedAsync();

                setupContext.Users.Add(new ApplicationUser
                {
                    Id = "user-01",
                    UserName = "user.one",
                    NormalizedUserName = "USER.ONE",
                    Email = "user.one@coinbot.local",
                    NormalizedEmail = "USER.ONE@COINBOT.LOCAL",
                    FullName = "User One",
                    TradingModeOverride = ExecutionEnvironment.Demo
                });

                await setupContext.SaveChangesAsync();
            }

            var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 3, 31, 9, 0, 0, TimeSpan.Zero));

            await using var context = new ApplicationDbContext(options, new TestDataScopeContext("user-01", hasIsolationBypass: false));
            var service = CreateUserExchangeCommandCenterService(
                context,
                timeProvider,
                environmentVariableName,
                new BinanceCredentialProbeSnapshot(
                    IsKeyValid: true,
                    CanTrade: true,
                    CanWithdraw: true,
                    SupportsSpot: true,
                    SupportsFutures: false,
                    HasTimestampSkew: false,
                    HasIpRestrictionIssue: false,
                    SpotEnvironmentScope: "Demo",
                    FuturesEnvironmentScope: "Demo",
                    PermissionSummary: "Trade=Y; Withdraw=Y; Spot=Y; Futures=N; Env=Demo",
                    SafeFailureReason: null),
                ExecutionEnvironment.Demo);

            var result = await service.ConnectBinanceAsync(
                new ConnectUserBinanceCredentialRequest(
                    "user-01",
                    ExchangeAccountId: null,
                    ApiKey: "api-key-int-001",
                    ApiSecret: "api-secret-int-001",
                    RequestedEnvironment: ExecutionEnvironment.Demo,
                    RequestedTradeMode: ExchangeTradeModeSelection.Spot,
                    Actor: "user:user-01",
                    CorrelationId: "corr-int-user-001"));

            Assert.False(result.IsValid);
            Assert.Contains("Withdraw", result.SafeFailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            await using var verifyContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true));
            var exchangeAccount = await verifyContext.ExchangeAccounts.SingleAsync(entity => entity.OwnerUserId == "user-01");
            var validation = await verifyContext.ApiCredentialValidations.SingleAsync(entity => entity.OwnerUserId == "user-01");
            var mirroredCredential = await verifyContext.ApiCredentials.SingleAsync(entity => entity.OwnerUserId == "user-01");

            Assert.Equal(ExchangeCredentialStatus.Invalid, exchangeAccount.CredentialStatus);
            Assert.True(validation.CanWithdraw);
            Assert.Equal("Invalid", validation.ValidationStatus);
            Assert.DoesNotContain("api-key-int-001", exchangeAccount.ApiKeyCiphertext ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("api-secret-int-001", mirroredCredential.ApiSecretCiphertext, StringComparison.Ordinal);

            var snapshot = await service.GetSnapshotAsync("user-01");

            Assert.Single(snapshot.Accounts);
            Assert.Single(snapshot.ValidationHistory);
            Assert.Equal("Geçersiz", snapshot.Accounts.Single().CredentialStatusLabel);
            Assert.Contains("Withdraw", snapshot.ValidationHistory.Single().FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await using var cleanupContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true));
            await cleanupContext.Database.EnsureDeletedAsync();
            Environment.SetEnvironmentVariable(environmentVariableName, previousEnvironmentValue);
        }
    }

    [Fact]
    public async Task UserExchangeCommandCenterService_FailsClosed_WhenDemoUserSubmitsLiveCredential()
    {
        var databaseName = $"CoinBotUserExchangeEnvGuardInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var options = CreateOptions(connectionString);
        var environmentVariableName = $"COINBOT_CREDENTIAL_INT_{Guid.NewGuid():N}";
        var previousEnvironmentValue = Environment.GetEnvironmentVariable(environmentVariableName);

        Environment.SetEnvironmentVariable(environmentVariableName, PrimaryKeyBase64);

        try
        {
            await using (var setupContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true)))
            {
                await setupContext.Database.EnsureDeletedAsync();
                await setupContext.Database.EnsureCreatedAsync();

                setupContext.Users.Add(new ApplicationUser
                {
                    Id = "user-guard-01",
                    UserName = "user.guard",
                    NormalizedUserName = "USER.GUARD",
                    Email = "user.guard@coinbot.local",
                    NormalizedEmail = "USER.GUARD@COINBOT.LOCAL",
                    FullName = "User Guard",
                    TradingModeOverride = ExecutionEnvironment.Demo
                });

                await setupContext.SaveChangesAsync();
            }

            var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 3, 31, 9, 10, 0, TimeSpan.Zero));

            await using var context = new ApplicationDbContext(options, new TestDataScopeContext("user-guard-01", hasIsolationBypass: false));
            var service = CreateUserExchangeCommandCenterService(
                context,
                timeProvider,
                environmentVariableName,
                new BinanceCredentialProbeSnapshot(
                    IsKeyValid: true,
                    CanTrade: true,
                    CanWithdraw: false,
                    SupportsSpot: true,
                    SupportsFutures: true,
                    HasTimestampSkew: false,
                    HasIpRestrictionIssue: false,
                    SpotEnvironmentScope: "Live",
                    FuturesEnvironmentScope: "Live",
                    PermissionSummary: "Trade=Y; Withdraw=N; Spot=Y; Futures=Y; Env=Live",
                    SafeFailureReason: null),
                ExecutionEnvironment.Demo);

            var result = await service.ConnectBinanceAsync(
                new ConnectUserBinanceCredentialRequest(
                    "user-guard-01",
                    ExchangeAccountId: null,
                    ApiKey: "api-key-int-guard",
                    ApiSecret: "api-secret-int-guard",
                    RequestedEnvironment: ExecutionEnvironment.Live,
                    RequestedTradeMode: ExchangeTradeModeSelection.Both,
                    Actor: "user:user-guard-01",
                    CorrelationId: "corr-int-user-guard-001"));

            Assert.False(result.IsValid);
            Assert.Contains("Demo", result.SafeFailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Live", result.SafeFailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            await using var verifyContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true));
            var validation = await verifyContext.ApiCredentialValidations.SingleAsync(entity => entity.OwnerUserId == "user-guard-01");

            Assert.False(validation.IsEnvironmentMatch);
            Assert.Equal("Live", validation.EnvironmentScope);
            Assert.Equal("Invalid", validation.ValidationStatus);
        }
        finally
        {
            await using var cleanupContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true));
            await cleanupContext.Database.EnsureDeletedAsync();
            Environment.SetEnvironmentVariable(environmentVariableName, previousEnvironmentValue);
        }
    }

    [Fact]
    public async Task UserExchangeCommandCenterService_FailsClosed_WhenProbeRequestThrows()
    {
        var databaseName = $"CoinBotUserExchangeProbeFailInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var options = CreateOptions(connectionString);
        var environmentVariableName = $"COINBOT_CREDENTIAL_INT_{Guid.NewGuid():N}";
        var previousEnvironmentValue = Environment.GetEnvironmentVariable(environmentVariableName);

        Environment.SetEnvironmentVariable(environmentVariableName, PrimaryKeyBase64);

        try
        {
            await using (var setupContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true)))
            {
                await setupContext.Database.EnsureDeletedAsync();
                await setupContext.Database.EnsureCreatedAsync();

                setupContext.Users.Add(new ApplicationUser
                {
                    Id = "user-probe-01",
                    UserName = "user.probe",
                    NormalizedUserName = "USER.PROBE",
                    Email = "user.probe@coinbot.local",
                    NormalizedEmail = "USER.PROBE@COINBOT.LOCAL",
                    FullName = "User Probe",
                    TradingModeOverride = ExecutionEnvironment.Demo
                });

                await setupContext.SaveChangesAsync();
            }

            var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 45, 0, TimeSpan.Zero));

            await using var context = new ApplicationDbContext(options, new TestDataScopeContext("user-probe-01", hasIsolationBypass: false));
            var service = CreateUserExchangeCommandCenterService(
                context,
                timeProvider,
                environmentVariableName,
                new ThrowingBinanceCredentialProbeClient(new HttpRequestException("Simulated probe outage.")),
                ExecutionEnvironment.Demo);

            var result = await service.ConnectBinanceAsync(
                new ConnectUserBinanceCredentialRequest(
                    "user-probe-01",
                    ExchangeAccountId: null,
                    ApiKey: "api-key-int-probe",
                    ApiSecret: "api-secret-int-probe",
                    RequestedEnvironment: ExecutionEnvironment.Demo,
                    RequestedTradeMode: ExchangeTradeModeSelection.Spot,
                    Actor: "user:user-probe-01",
                    CorrelationId: "corr-int-user-probe-001"));

            Assert.False(result.IsValid);
            Assert.Contains("ulaşılamadı", result.SafeFailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            await using var verifyContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true));
            var exchangeAccount = await verifyContext.ExchangeAccounts.SingleAsync(entity => entity.OwnerUserId == "user-probe-01");
            var validation = await verifyContext.ApiCredentialValidations.SingleAsync(entity => entity.OwnerUserId == "user-probe-01");
            var mirroredCredential = await verifyContext.ApiCredentials.SingleAsync(entity => entity.OwnerUserId == "user-probe-01");

            Assert.Equal(ExchangeCredentialStatus.Invalid, exchangeAccount.CredentialStatus);
            Assert.Equal("Invalid", validation.ValidationStatus);
            Assert.Equal("Trade=?; Withdraw=?; Spot=?; Futures=?; Env=Unknown", validation.PermissionSummary);
            Assert.Contains("ulaşılamadı", validation.FailureReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("api-key-int-probe", exchangeAccount.ApiKeyCiphertext ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("api-secret-int-probe", mirroredCredential.ApiSecretCiphertext, StringComparison.Ordinal);
        }
        finally
        {
            await using var cleanupContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true));
            await cleanupContext.Database.EnsureDeletedAsync();
            Environment.SetEnvironmentVariable(environmentVariableName, previousEnvironmentValue);
        }
    }

    [Fact]
    public async Task AdminWorkspaceReadModelService_UserDetail_ReadsExchangeAndValidationDataAcrossUserScope()
    {
        var databaseName = $"CoinBotAdminUserHQInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var options = CreateOptions(connectionString);
        var validatedAtUtc = new DateTime(2026, 3, 31, 8, 30, 0, DateTimeKind.Utc);

        try
        {
            await using (var setupContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true)))
            {
                await setupContext.Database.EnsureDeletedAsync();
                await setupContext.Database.EnsureCreatedAsync();

                setupContext.Users.AddRange(
                    new ApplicationUser
                    {
                        Id = "admin-01",
                        UserName = "admin.one",
                        NormalizedUserName = "ADMIN.ONE",
                        Email = "admin.one@coinbot.local",
                        NormalizedEmail = "ADMIN.ONE@COINBOT.LOCAL",
                        FullName = "Admin One"
                    },
                    new ApplicationUser
                    {
                        Id = "user-02",
                        UserName = "user.two",
                        NormalizedUserName = "USER.TWO",
                        Email = "user.two@coinbot.local",
                        NormalizedEmail = "USER.TWO@COINBOT.LOCAL",
                        FullName = "User Two",
                        TradingModeOverride = ExecutionEnvironment.Live,
                        TradingModeApprovedAtUtc = validatedAtUtc
                    });

                var exchangeAccount = new ExchangeAccount
                {
                    OwnerUserId = "user-02",
                    ExchangeName = "Binance",
                    DisplayName = "Main Binance",
                    IsReadOnly = false,
                    CredentialStatus = ExchangeCredentialStatus.Active,
                    CredentialFingerprint = "ABCDEF1234567890",
                    LastValidatedAt = validatedAtUtc
                };
                setupContext.ExchangeAccounts.Add(exchangeAccount);
                await setupContext.SaveChangesAsync();

                var apiCredential = new ApiCredential
                {
                    ExchangeAccountId = exchangeAccount.Id,
                    OwnerUserId = "user-02",
                    ApiKeyCiphertext = "cipher-key",
                    ApiSecretCiphertext = "cipher-secret",
                    CredentialFingerprint = "ABCDEF1234567890",
                    KeyVersion = "credential-v1",
                    EncryptedBlobVersion = 1,
                    ValidationStatus = "Valid",
                    PermissionSummary = "Trade=Y; Withdraw=N; Spot=Y; Futures=Y; Env=Live",
                    StoredAtUtc = validatedAtUtc.AddMinutes(-5),
                    LastValidatedAtUtc = validatedAtUtc
                };
                setupContext.ApiCredentials.Add(apiCredential);
                setupContext.ApiCredentialValidations.Add(new ApiCredentialValidation
                {
                    ApiCredentialId = apiCredential.Id,
                    ExchangeAccountId = exchangeAccount.Id,
                    OwnerUserId = "user-02",
                    IsKeyValid = true,
                    CanTrade = true,
                    CanWithdraw = false,
                    SupportsSpot = true,
                    SupportsFutures = true,
                    EnvironmentScope = "Live",
                    IsEnvironmentMatch = true,
                    HasTimestampSkew = false,
                    HasIpRestrictionIssue = false,
                    ValidationStatus = "Valid",
                    PermissionSummary = "Trade=Y; Withdraw=N; Spot=Y; Futures=Y; Env=Live",
                    FailureReason = null,
                    ValidatedAtUtc = validatedAtUtc
                });
                setupContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
                {
                    OwnerUserId = "user-02",
                    ExchangeAccountId = exchangeAccount.Id,
                    PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
                    DriftStatus = ExchangeStateDriftStatus.InSync
                });
                setupContext.RiskProfiles.Add(new RiskProfile
                {
                    OwnerUserId = "user-02",
                    ProfileName = "Core",
                    MaxDailyLossPercentage = 2m,
                    MaxPositionSizePercentage = 10m,
                    MaxLeverage = 3m,
                    KillSwitchEnabled = false
                });
                setupContext.UserExecutionOverrides.Add(new UserExecutionOverride
                {
                    UserId = "user-02",
                    ReduceOnly = true,
                    MaxDailyTrades = 4
                });

                await setupContext.SaveChangesAsync();
            }

            await using var readContext = new ApplicationDbContext(options, new TestDataScopeContext("admin-01", hasIsolationBypass: false));
            var readModelService = new AdminWorkspaceReadModelService(
                readContext,
                new FakeAdminMonitoringReadModelService(),
                new FakeTradingModeResolver(ExecutionEnvironment.Live, hasLiveApproval: true),
                new FixedTimeProvider(new DateTimeOffset(2026, 3, 31, 9, 0, 0, TimeSpan.Zero)));

            var snapshot = await readModelService.GetUserDetailAsync("user-02");

            Assert.NotNull(snapshot);
            Assert.Equal("Live", snapshot!.Environment.EffectiveEnvironmentLabel);
            Assert.Equal("Core", snapshot.RiskOverride.RiskProfileName);
            Assert.Single(snapshot.ExchangeAccounts);
            Assert.Single(snapshot.ValidationHistory);
            var exchangeSnapshot = snapshot.ExchangeAccounts.Single();

            Assert.Equal("Trade=Y; Withdraw=N; Spot=Y; Futures=Y; Env=Live", exchangeSnapshot.PermissionSummary);
            Assert.Equal("Live", exchangeSnapshot.EnvironmentLabel);
            Assert.Equal("Bağlı", exchangeSnapshot.SyncStatus);
        }
        finally
        {
            await using var cleanupContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true));
            await cleanupContext.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task AdminWorkspaceReadModelService_UsersList_ReadsRealUserRowsAndExchangeCounts()
    {
        var databaseName = $"CoinBotAdminUsersListInt_{Guid.NewGuid():N}";
        var connectionString = ResolveConnectionString(databaseName);
        var options = CreateOptions(connectionString);

        try
        {
            await using (var setupContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true)))
            {
                await setupContext.Database.EnsureDeletedAsync();
                await setupContext.Database.EnsureCreatedAsync();

                setupContext.Users.AddRange(
                    new ApplicationUser
                    {
                        Id = "admin-list-01",
                        UserName = "admin.list",
                        NormalizedUserName = "ADMIN.LIST",
                        Email = "admin.list@coinbot.local",
                        NormalizedEmail = "ADMIN.LIST@COINBOT.LOCAL",
                        FullName = "Admin List"
                    },
                    new ApplicationUser
                    {
                        Id = "user-list-01",
                        UserName = "user.list",
                        NormalizedUserName = "USER.LIST",
                        Email = "user.list@coinbot.local",
                        NormalizedEmail = "USER.LIST@COINBOT.LOCAL",
                        FullName = "User List"
                    });
                setupContext.ExchangeAccounts.Add(new ExchangeAccount
                {
                    OwnerUserId = "user-list-01",
                    ExchangeName = "Binance",
                    DisplayName = "List Binance",
                    IsReadOnly = false,
                    CredentialStatus = ExchangeCredentialStatus.Active,
                    CredentialFingerprint = "FFEEDDCCBBAA9988"
                });

                await setupContext.SaveChangesAsync();
            }

            await using var readContext = new ApplicationDbContext(options, new TestDataScopeContext("admin-list-01", hasIsolationBypass: false));
            var readModelService = new AdminWorkspaceReadModelService(
                readContext,
                new FakeAdminMonitoringReadModelService(),
                new FakeTradingModeResolver(ExecutionEnvironment.Demo, hasLiveApproval: false),
                new FixedTimeProvider(new DateTimeOffset(2026, 3, 31, 9, 15, 0, TimeSpan.Zero)));

            var snapshot = await readModelService.GetUsersAsync();
            var userRow = Assert.Single(snapshot.Users, item => item.UserId == "user-list-01");

            Assert.Equal("User List", userRow.DisplayName);
            Assert.Equal(1, userRow.ExchangeCount);
        }
        finally
        {
            await using var cleanupContext = new ApplicationDbContext(options, new TestDataScopeContext(null, hasIsolationBypass: true));
            await cleanupContext.Database.EnsureDeletedAsync();
        }
    }

    private static UserExchangeCommandCenterService CreateUserExchangeCommandCenterService(
        ApplicationDbContext dbContext,
        TimeProvider timeProvider,
        string environmentVariableName,
        BinanceCredentialProbeSnapshot probeSnapshot,
        ExecutionEnvironment effectiveEnvironment)
    {
        return CreateUserExchangeCommandCenterService(
            dbContext,
            timeProvider,
            environmentVariableName,
            new FakeBinanceCredentialProbeClient(probeSnapshot),
            effectiveEnvironment);
    }

    private static UserExchangeCommandCenterService CreateUserExchangeCommandCenterService(
        ApplicationDbContext dbContext,
        TimeProvider timeProvider,
        string environmentVariableName,
        IBinanceCredentialProbeClient probeClient,
        ExecutionEnvironment effectiveEnvironment)
    {
        var credentialSecurityOptions = Options.Create(new CredentialSecurityOptions
        {
            Provider = CredentialSecurityKeyProvider.Environment,
            KeyVersion = "credential-v1",
            EnvironmentVariableName = environmentVariableName,
            RevalidationIntervalDays = 30,
            RotationIntervalDays = 90
        });
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var credentialCipher = new Aes256CredentialCipher(
            new CredentialKeyResolver(new ConfigurationBuilder().Build(), credentialSecurityOptions));
        var validationService = new ApiCredentialValidationService(dbContext, timeProvider);
        var exchangeCredentialService = new ExchangeCredentialService(
            dbContext,
            credentialCipher,
            validationService,
            auditLogService,
            credentialSecurityOptions,
            timeProvider);

        return new UserExchangeCommandCenterService(
            dbContext,
            exchangeCredentialService,
            new FakeTradingModeResolver(effectiveEnvironment, hasLiveApproval: effectiveEnvironment == ExecutionEnvironment.Live),
            probeClient,
            timeProvider);
    }

    private static DbContextOptions<ApplicationDbContext> CreateOptions(string connectionString)
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;
    }

    private static string ResolveConnectionString(string databaseName)
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable("COINBOT_INTEGRATION_SQLSERVER_CONNECTION_STRING");

        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString.Replace("{Database}", databaseName, StringComparison.OrdinalIgnoreCase);
        }

        return $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True";
    }

    private const string PrimaryKeyBase64 = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

    private sealed class TestDataScopeContext(string? userId, bool hasIsolationBypass) : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => userId;

        public bool HasIsolationBypass => hasIsolationBypass;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeBinanceCredentialProbeClient(BinanceCredentialProbeSnapshot snapshot) : IBinanceCredentialProbeClient
    {
        public Task<BinanceCredentialProbeSnapshot> ProbeAsync(string apiKey, string apiSecret, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(snapshot);
        }
    }

    private sealed class ThrowingBinanceCredentialProbeClient(Exception exception) : IBinanceCredentialProbeClient
    {
        public Task<BinanceCredentialProbeSnapshot> ProbeAsync(string apiKey, string apiSecret, CancellationToken cancellationToken = default)
        {
            return Task.FromException<BinanceCredentialProbeSnapshot>(exception);
        }
    }

    private sealed class FakeTradingModeResolver(ExecutionEnvironment effectiveMode, bool hasLiveApproval) : ITradingModeResolver
    {
        public Task<TradingModeResolution> ResolveAsync(TradingModeResolutionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TradingModeResolution(
                GlobalDefaultMode: ExecutionEnvironment.Demo,
                UserOverrideMode: effectiveMode,
                BotOverrideMode: null,
                StrategyPublishedMode: null,
                EffectiveMode: effectiveMode,
                ResolutionSource: TradingModeResolutionSource.UserOverride,
                Reason: $"User override resolves to {effectiveMode}.",
                HasExplicitLiveApproval: hasLiveApproval));
        }
    }

    private sealed class FakeAdminMonitoringReadModelService : IAdminMonitoringReadModelService
    {
        public Task<MonitoringDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MonitoringDashboardSnapshot.Empty(new DateTime(2026, 3, 31, 9, 0, 0, DateTimeKind.Utc)));
        }
    }
}
