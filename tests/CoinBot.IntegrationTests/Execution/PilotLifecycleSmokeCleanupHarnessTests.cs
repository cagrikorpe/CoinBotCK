using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CoinBot.IntegrationTests.Execution;

public sealed class PilotLifecycleSmokeCleanupHarnessTests
{
    private const string WorkerUserSecretsId = "016c8a65-b0e7-404b-a04c-0a51f7bea920";
    [Fact]
    public async Task RunAsync_WhenRequested()
    {
        var request = CleanupRequest.TryResolve();

        if (request is null)
        {
            return;
        }

        await using var provider = BuildProvider(request.ConnectionString);
        await using var asyncScope = provider.CreateAsyncScope();
        var scopeAccessor = asyncScope.ServiceProvider.GetRequiredService<IDataScopeContextAccessor>();
        using var isolationBypass = scopeAccessor.BeginScope(hasIsolationBypass: true);

        var result = await RunCleanupAsync(asyncScope.ServiceProvider, request, CancellationToken.None);

        Console.WriteLine(result.Summary);
        Assert.True(result.IsSuccessful, result.Summary);
    }

    private static async Task<CleanupResult> RunCleanupAsync(
        IServiceProvider serviceProvider,
        CleanupRequest request,
        CancellationToken cancellationToken)
    {
        var crisisEscalationService = serviceProvider.GetRequiredService<ICrisisEscalationService>();
        var dbContext = serviceProvider.GetRequiredService<ApplicationDbContext>();
        var executionReconciliationService = serviceProvider.GetRequiredService<ExecutionReconciliationService>();
        var exchangeAppStateSyncService = serviceProvider.GetRequiredService<ExchangeAppStateSyncService>();
        var exchangeCredentialService = serviceProvider.GetRequiredService<IExchangeCredentialService>();
        var privateRestClient = serviceProvider.GetRequiredService<IBinancePrivateRestClient>();
        var balanceSyncService = serviceProvider.GetRequiredService<ExchangeBalanceSyncService>();
        var positionSyncService = serviceProvider.GetRequiredService<ExchangePositionSyncService>();

        await SyncScopedAccountsAsync(
            dbContext,
            request.ScopeUserId,
            exchangeCredentialService,
            privateRestClient,
            balanceSyncService,
            positionSyncService,
            cancellationToken);
        await exchangeAppStateSyncService.RunOnceAsync(cancellationToken);

        var previewRequest = new CrisisEscalationPreviewRequest(
            CrisisEscalationLevel.EmergencyFlatten,
            request.Scope);
        var preview = await crisisEscalationService.PreviewAsync(previewRequest, cancellationToken);

        if (preview.PendingOrderCount == 0 && preview.OpenPositionCount == 0)
        {
            return new CleanupResult(
                true,
                $"Phase={request.Phase} | Scope={request.Scope} | Status=NoOp | PendingOrders=0 | OpenPositions=0");
        }

        var execution = await crisisEscalationService.ExecuteAsync(
            new CrisisEscalationExecuteRequest(
                CrisisEscalationLevel.EmergencyFlatten,
                request.Scope,
                CommandId: $"pilot-smoke-cleanup:{request.Phase}:{Guid.NewGuid():N}",
                ActorUserId: "pilot-smoke-cleanup",
                ExecutionActor: "system:pilot-smoke-cleanup",
                Reason: $"Pilot smoke {request.Phase} cleanup",
                ReasonCode: "CRISIS_EMERGENCY_FLATTEN",
                Message: $"Pilot smoke {request.Phase} cleanup",
                PreviewStamp: preview.PreviewStamp,
                CorrelationId: $"pilot-smoke-cleanup-{Guid.NewGuid():N}",
                ReauthToken: "pilot-smoke-reauth",
                SecondApprovalReference: "pilot-smoke-approval",
                RemoteIpAddress: null),
            cancellationToken);

        var timeoutAtUtc = DateTime.UtcNow.AddSeconds(request.TimeoutSeconds);
        var latestPreview = preview;
        var lastReconciledCount = 0;

        while (DateTime.UtcNow < timeoutAtUtc)
        {
            lastReconciledCount = await executionReconciliationService.RunOnceAsync(cancellationToken);
            await SyncScopedAccountsAsync(
                dbContext,
                request.ScopeUserId,
                exchangeCredentialService,
                privateRestClient,
                balanceSyncService,
                positionSyncService,
                cancellationToken);
            await exchangeAppStateSyncService.RunOnceAsync(cancellationToken);

            latestPreview = await crisisEscalationService.PreviewAsync(previewRequest, cancellationToken);

            if (latestPreview.PendingOrderCount == 0 && latestPreview.OpenPositionCount == 0)
            {
                return new CleanupResult(
                    true,
                    $"Phase={request.Phase} | Scope={request.Scope} | Status=Closed | PendingOrders=0 | OpenPositions=0 | ReconciledCount={lastReconciledCount} | ExecuteSummary={execution.Summary}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return new CleanupResult(
            false,
            $"Phase={request.Phase} | Scope={request.Scope} | Status=TimedOut | PendingOrders={latestPreview.PendingOrderCount} | OpenPositions={latestPreview.OpenPositionCount} | ReconciledCount={lastReconciledCount} | ExecuteSummary={execution.Summary}");
    }

    private static async Task SyncScopedAccountsAsync(
        ApplicationDbContext dbContext,
        string? scopeUserId,
        IExchangeCredentialService exchangeCredentialService,
        IBinancePrivateRestClient privateRestClient,
        ExchangeBalanceSyncService balanceSyncService,
        ExchangePositionSyncService positionSyncService,
        CancellationToken cancellationToken)
    {
        var accounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.CredentialStatus == ExchangeCredentialStatus.Active &&
                (scopeUserId == null || entity.OwnerUserId == scopeUserId))
            .Select(entity => new AccountDescriptor(
                entity.Id,
                entity.OwnerUserId,
                entity.ExchangeName))
            .ToListAsync(cancellationToken);

        foreach (var account in accounts)
        {
            var credentialAccess = await exchangeCredentialService.GetAsync(
                new ExchangeCredentialAccessRequest(
                    account.ExchangeAccountId,
                    "system:pilot-smoke-cleanup",
                    ExchangeCredentialAccessPurpose.Synchronization),
                cancellationToken);
            var snapshot = await privateRestClient.GetAccountSnapshotAsync(
                account.ExchangeAccountId,
                account.OwnerUserId,
                account.ExchangeName,
                credentialAccess.ApiKey,
                credentialAccess.ApiSecret,
                cancellationToken);

            await balanceSyncService.ApplyAsync(snapshot, cancellationToken);
            await positionSyncService.ApplyAsync(snapshot, cancellationToken);
        }
    }

    private static ServiceProvider BuildProvider(string connectionString)
    {
        var repoRoot = ResolveRepoRoot();
        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(repoRoot)
            .AddJsonFile("src/CoinBot.Worker/appsettings.json", optional: false)
            .AddJsonFile("src/CoinBot.Worker/appsettings.Development.json", optional: true);

        var workerUserSecretsPath = ResolveWorkerUserSecretsPath();
        if (File.Exists(workerUserSecretsPath))
        {
            configurationBuilder.AddJsonFile(workerUserSecretsPath, optional: true);
        }

        var configuration = configurationBuilder
            .AddEnvironmentVariables()
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
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment("Development", repoRoot));
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CoinBot.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repo root could not be resolved for pilot smoke cleanup harness.");
    }

    private static string ResolveWorkerUserSecretsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "UserSecrets",
            WorkerUserSecretsId,
            "secrets.json");
    }

    private sealed record CleanupRequest(string ConnectionString, string Scope, string? ScopeUserId, string Phase, int TimeoutSeconds)
    {
        private const string EnabledEnvironmentVariable = "PILOT_SMOKE_CLEANUP_ENABLED";
        private const string ConnectionStringEnvironmentVariable = "PILOT_SMOKE_CLEANUP_CONNECTION_STRING";
        private const string ScopeEnvironmentVariable = "PILOT_SMOKE_CLEANUP_SCOPE";
        private const string PhaseEnvironmentVariable = "PILOT_SMOKE_CLEANUP_PHASE";
        private const string TimeoutEnvironmentVariable = "PILOT_SMOKE_CLEANUP_TIMEOUT_SECONDS";

        public static CleanupRequest? TryResolve()
        {
            var enabled = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);

            if (!bool.TryParse(enabled, out var isEnabled) || !isEnabled)
            {
                return null;
            }

            var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)?.Trim();
            var scope = Environment.GetEnvironmentVariable(ScopeEnvironmentVariable)?.Trim();
            var phase = Environment.GetEnvironmentVariable(PhaseEnvironmentVariable)?.Trim();
            var timeoutValue = Environment.GetEnvironmentVariable(TimeoutEnvironmentVariable)?.Trim();

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"{ConnectionStringEnvironmentVariable} is required.");
            }

            if (string.IsNullOrWhiteSpace(scope))
            {
                throw new InvalidOperationException($"{ScopeEnvironmentVariable} is required.");
            }

            var timeoutSeconds = int.TryParse(timeoutValue, out var parsedTimeoutSeconds) && parsedTimeoutSeconds >= 30
                ? parsedTimeoutSeconds
                : 240;

            return new CleanupRequest(
                connectionString,
                scope,
                scope.StartsWith("FLATTEN:USER:", StringComparison.OrdinalIgnoreCase)
                    ? scope["FLATTEN:USER:".Length..].Trim()
                    : scope.StartsWith("PURGE:USER:", StringComparison.OrdinalIgnoreCase)
                        ? scope["PURGE:USER:".Length..].Trim()
                        : null,
                string.IsNullOrWhiteSpace(phase) ? "cleanup" : phase,
                timeoutSeconds);
        }
    }

    private sealed record CleanupResult(bool IsSuccessful, string Summary);

    private sealed record AccountDescriptor(Guid ExchangeAccountId, string OwnerUserId, string ExchangeName);

    private sealed class TestHostEnvironment(string environmentName, string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "CoinBot.IntegrationTests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(contentRootPath);
    }
}






