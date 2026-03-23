using System.Text.Json;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Credentials;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Credentials;

public sealed class ExchangeCredentialServiceTests
{
    [Fact]
    public async Task StoreAsync_EncryptsCredentials_And_AuditsWithoutPlaintext()
    {
        await using var harness = CreateHarness();
        var exchangeAccountId = await CreateExchangeAccountAsync(harness.DbContext);

        var state = await harness.Service.StoreAsync(
            new StoreExchangeCredentialsRequest(
                exchangeAccountId,
                "api-key-001",
                "api-secret-001",
                "user-01",
                "corr-store-001"));

        var exchangeAccount = await harness.DbContext.ExchangeAccounts.SingleAsync(entity => entity.Id == exchangeAccountId);
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync();

        Assert.Equal(ExchangeCredentialStatus.PendingValidation, state.Status);
        Assert.NotNull(exchangeAccount.ApiKeyCiphertext);
        Assert.NotNull(exchangeAccount.ApiSecretCiphertext);
        Assert.DoesNotContain("api-key-001", exchangeAccount.ApiKeyCiphertext!, StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret-001", exchangeAccount.ApiSecretCiphertext!, StringComparison.Ordinal);
        Assert.NotNull(exchangeAccount.CredentialFingerprint);
        Assert.Equal("credential-v1", exchangeAccount.CredentialKeyVersion);
        Assert.Equal(ExchangeCredentialStatus.PendingValidation, exchangeAccount.CredentialStatus);
        Assert.Equal("user-01", auditLog.Actor);
        Assert.Equal("ExchangeCredential.Stored", auditLog.Action);
        Assert.Equal("corr-store-001", auditLog.CorrelationId);
        Assert.Equal("Applied", auditLog.Outcome);
        Assert.Contains("AccessMode=EncryptedWrite", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("Materials=ApiKey,ApiSecret", auditLog.Context, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-001", auditLog.Context ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret-001", auditLog.Context ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_ForExecution_FailsClosed_WhenCredentialIsPendingValidation()
    {
        await using var harness = CreateHarness();
        var exchangeAccountId = await CreateExchangeAccountAsync(harness.DbContext);

        await harness.Service.StoreAsync(
            new StoreExchangeCredentialsRequest(
                exchangeAccountId,
                "api-key-002",
                "api-secret-002",
                "user-02",
                "corr-store-002"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.GetAsync(
                new ExchangeCredentialAccessRequest(
                    exchangeAccountId,
                    "user-02",
                    ExchangeCredentialAccessPurpose.Execution,
                    "corr-exec-002")));

        var auditLogs = await harness.DbContext.AuditLogs
            .OrderBy(entity => entity.CreatedDate)
            .ToListAsync();

        Assert.Contains("pending validation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, auditLogs.Count);
        Assert.Equal("Blocked:PendingValidation", auditLogs[^1].Outcome);
    }

    [Fact]
    public async Task GetAsync_ForSynchronization_FailsClosed_WhenCredentialIsPendingValidation()
    {
        await using var harness = CreateHarness();
        var exchangeAccountId = await CreateExchangeAccountAsync(harness.DbContext);

        await harness.Service.StoreAsync(
            new StoreExchangeCredentialsRequest(
                exchangeAccountId,
                "api-key-sync-001",
                "api-secret-sync-001",
                "user-sync-01",
                "corr-store-sync-001"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.GetAsync(
                new ExchangeCredentialAccessRequest(
                    exchangeAccountId,
                    "user-sync-01",
                    ExchangeCredentialAccessPurpose.Synchronization,
                    "corr-sync-001")));

        Assert.Contains("Synchronization blocked", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetValidationStateAsync_AllowsExecutionAccess_AfterSuccessfulValidation()
    {
        await using var harness = CreateHarness();
        var exchangeAccountId = await CreateExchangeAccountAsync(harness.DbContext);

        await harness.Service.StoreAsync(
            new StoreExchangeCredentialsRequest(
                exchangeAccountId,
                "api-key-003",
                "api-secret-003",
                "user-03",
                "corr-store-003"));

        var validationState = await harness.Service.SetValidationStateAsync(
            new SetExchangeCredentialValidationStateRequest(
                exchangeAccountId,
                true,
                "user-03",
                "corr-validate-003"));

        var accessResult = await harness.Service.GetAsync(
            new ExchangeCredentialAccessRequest(
                exchangeAccountId,
                "user-03",
                ExchangeCredentialAccessPurpose.Execution,
                "corr-exec-003"));

        var exchangeAccount = await harness.DbContext.ExchangeAccounts.SingleAsync(entity => entity.Id == exchangeAccountId);
        var auditLogs = await harness.DbContext.AuditLogs
            .OrderBy(entity => entity.CreatedDate)
            .ToListAsync();

        Assert.Equal(ExchangeCredentialStatus.Active, validationState.Status);
        Assert.Equal("api-key-003", accessResult.ApiKey);
        Assert.Equal("api-secret-003", accessResult.ApiSecret);
        Assert.Equal(ExchangeCredentialStatus.Active, accessResult.State.Status);
        Assert.NotNull(exchangeAccount.CredentialLastAccessedAtUtc);
        Assert.Equal("Validated", auditLogs[1].Outcome);
        Assert.Equal("corr-exec-003", auditLogs[^1].CorrelationId);
        Assert.Equal("Allowed", auditLogs[^1].Outcome);
        Assert.Contains("Purpose=Execution", auditLogs[^1].Context, StringComparison.Ordinal);
        Assert.Contains("AccessMode=DecryptRead", auditLogs[^1].Context, StringComparison.Ordinal);
        Assert.Contains("Materials=ApiKey,ApiSecret", auditLogs[^1].Context, StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret-003", auditLogs[^1].Context ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretCarrierTypes_RedactSecrets_FromToString_AndSerializedAccessResponse()
    {
        var storeRequest = new StoreExchangeCredentialsRequest(
            Guid.NewGuid(),
            "api-key-store",
            "api-secret-store",
            "user-redact",
            "corr-redact-1");
        var accessResult = new ExchangeCredentialAccessResult(
            "api-key-access",
            "api-secret-access",
            new ExchangeCredentialStateSnapshot(
                Guid.NewGuid(),
                ExchangeCredentialStatus.Active,
                "fingerprint",
                "credential-v1",
                StoredAtUtc: new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc),
                LastValidatedAtUtc: null,
                LastAccessedAtUtc: null,
                LastRotatedAtUtc: null,
                RevalidateAfterUtc: null,
                RotateAfterUtc: null));
        var placementRequest = new BinanceOrderPlacementRequest(
            Guid.NewGuid(),
            "BTCUSDT",
            ExecutionOrderSide.Buy,
            ExecutionOrderType.Market,
            0.1m,
            65000m,
            "client-order-1",
            "api-key-place",
            "api-secret-place");
        var queryRequest = new BinanceOrderQueryRequest(
            Guid.NewGuid(),
            "BTCUSDT",
            "exchange-order-1",
            "client-order-1",
            "api-key-query",
            "api-secret-query");

        var serializedAccessResult = JsonSerializer.Serialize(accessResult);

        Assert.DoesNotContain("api-key-store", storeRequest.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret-store", storeRequest.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-access", accessResult.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret-access", accessResult.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-place", placementRequest.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret-place", placementRequest.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-query", queryRequest.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret-query", queryRequest.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-access", serializedAccessResult, StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret-access", serializedAccessResult, StringComparison.Ordinal);
        Assert.Contains("\"State\":", serializedAccessResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_FailsClosed_WhenEncryptionKeyChanges()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        var databaseName = Guid.NewGuid().ToString("N");
        var exchangeAccountId = Guid.Empty;

        await using (var firstHarness = CreateHarness(databaseName, databaseRoot, keyMaterialBase64: PrimaryKeyBase64))
        {
            exchangeAccountId = await CreateExchangeAccountAsync(firstHarness.DbContext);

            await firstHarness.Service.StoreAsync(
                new StoreExchangeCredentialsRequest(
                    exchangeAccountId,
                    "api-key-004",
                    "api-secret-004",
                    "user-04",
                    "corr-store-004"));

            await firstHarness.Service.SetValidationStateAsync(
                new SetExchangeCredentialValidationStateRequest(
                    exchangeAccountId,
                    true,
                    "user-04",
                    "corr-validate-004"));
        }

        await using var secondHarness = CreateHarness(databaseName, databaseRoot, keyMaterialBase64: SecondaryKeyBase64);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            secondHarness.Service.GetAsync(
                new ExchangeCredentialAccessRequest(
                    exchangeAccountId,
                    "user-04",
                    ExchangeCredentialAccessPurpose.Execution,
                    "corr-exec-004")));

        var exchangeAccount = await secondHarness.DbContext.ExchangeAccounts.SingleAsync(entity => entity.Id == exchangeAccountId);
        var auditLog = await secondHarness.DbContext.AuditLogs
            .OrderByDescending(entity => entity.CreatedDate)
            .FirstAsync();

        Assert.Contains("decryption failed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExchangeCredentialStatus.Invalid, exchangeAccount.CredentialStatus);
        Assert.Equal("Blocked:DecryptFailed", auditLog.Outcome);
    }

    [Fact]
    public async Task GetAsync_FailsClosedForExecution_WhenRevalidationWindowExpires_ButAllowsValidationAccess()
    {
        await using var harness = CreateHarness(revalidationIntervalDays: 1, rotationIntervalDays: 10);
        var exchangeAccountId = await CreateExchangeAccountAsync(harness.DbContext);

        await harness.Service.StoreAsync(
            new StoreExchangeCredentialsRequest(
                exchangeAccountId,
                "api-key-005",
                "api-secret-005",
                "user-05",
                "corr-store-005"));

        await harness.Service.SetValidationStateAsync(
            new SetExchangeCredentialValidationStateRequest(
                exchangeAccountId,
                true,
                "user-05",
                "corr-validate-005"));

        harness.TimeProvider.Advance(TimeSpan.FromDays(2));

        var executionException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.GetAsync(
                new ExchangeCredentialAccessRequest(
                    exchangeAccountId,
                    "user-05",
                    ExchangeCredentialAccessPurpose.Execution,
                    "corr-exec-005")));

        var validationResult = await harness.Service.GetAsync(
            new ExchangeCredentialAccessRequest(
                exchangeAccountId,
                "user-05",
                ExchangeCredentialAccessPurpose.Validation,
                "corr-revalidate-005"));

        var exchangeAccount = await harness.DbContext.ExchangeAccounts.SingleAsync(entity => entity.Id == exchangeAccountId);

        Assert.Contains("re-validated", executionException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExchangeCredentialStatus.RevalidationRequired, exchangeAccount.CredentialStatus);
        Assert.Equal("api-key-005", validationResult.ApiKey);
        Assert.Equal("api-secret-005", validationResult.ApiSecret);
        Assert.Equal(ExchangeCredentialStatus.RevalidationRequired, validationResult.State.Status);
    }

    private static async Task<Guid> CreateExchangeAccountAsync(ApplicationDbContext dbContext)
    {
        var exchangeAccount = new ExchangeAccount
        {
            OwnerUserId = "user-owner",
            ExchangeName = "Binance",
            DisplayName = "Primary",
            IsReadOnly = false
        };

        dbContext.ExchangeAccounts.Add(exchangeAccount);
        await dbContext.SaveChangesAsync();
        return exchangeAccount.Id;
    }

    private static TestHarness CreateHarness(
        string? databaseName = null,
        InMemoryDatabaseRoot? databaseRoot = null,
        string? keyMaterialBase64 = null,
        int revalidationIntervalDays = 30,
        int rotationIntervalDays = 90)
    {
        var environmentVariableName = $"COINBOT_CREDENTIAL_TEST_{Guid.NewGuid():N}";
        var previousEnvironmentValue = Environment.GetEnvironmentVariable(environmentVariableName);
        Environment.SetEnvironmentVariable(environmentVariableName, keyMaterialBase64 ?? PrimaryKeyBase64);

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"), databaseRoot ?? new InMemoryDatabaseRoot())
            .Options;

        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var configuration = new ConfigurationBuilder().Build();
        var securityOptions = Options.Create(new CredentialSecurityOptions
        {
            Provider = CredentialSecurityKeyProvider.Environment,
            KeyVersion = "credential-v1",
            EnvironmentVariableName = environmentVariableName,
            RevalidationIntervalDays = revalidationIntervalDays,
            RotationIntervalDays = rotationIntervalDays
        });
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var keyResolver = new CredentialKeyResolver(configuration, securityOptions);
        var credentialCipher = new Aes256CredentialCipher(keyResolver);
        var service = new ExchangeCredentialService(dbContext, credentialCipher, auditLogService, securityOptions, timeProvider);

        return new TestHarness(dbContext, service, timeProvider, environmentVariableName, previousEnvironmentValue);
    }

    private const string PrimaryKeyBase64 = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";
    private const string SecondaryKeyBase64 = "ICEeHRwbGhkYFxYVFBMSERAPDg0MCwoJCAcGBQQDAgE=";

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        IExchangeCredentialService service,
        AdjustableTimeProvider timeProvider,
        string environmentVariableName,
        string? previousEnvironmentValue) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public IExchangeCredentialService Service { get; } = service;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            Environment.SetEnvironmentVariable(environmentVariableName, previousEnvironmentValue);
        }
    }
}
