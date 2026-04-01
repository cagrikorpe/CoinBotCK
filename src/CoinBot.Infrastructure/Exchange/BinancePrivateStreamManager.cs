using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Exchange;

public sealed class BinancePrivateStreamManager(
    IServiceScopeFactory serviceScopeFactory,
    IBinancePrivateRestClient privateRestClient,
    IBinancePrivateStreamClient privateStreamClient,
    ExchangeAccountSnapshotHub snapshotHub,
    IOptions<BinancePrivateDataOptions> options,
    TimeProvider timeProvider,
    ILogger<BinancePrivateStreamManager> logger) : BackgroundService
{
    private const string SystemActor = "system:private-stream-manager";
    private readonly BinancePrivateDataOptions optionsValue = options.Value;
    private readonly Dictionary<Guid, SessionRegistration> sessions = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Binance private stream manager starting. Enabled={Enabled}.",
            optionsValue.Enabled);

        if (!optionsValue.Enabled)
        {
            logger.LogInformation("Binance private stream manager is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshSessionsAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(optionsValue.SessionScanIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                logger.LogWarning("Binance private stream manager refresh failed.");
                await Task.Delay(TimeSpan.FromSeconds(optionsValue.ReconnectDelaySeconds), stoppingToken);
            }
        }

        foreach (var registration in sessions.Values.ToArray())
        {
            registration.CancellationTokenSource.Cancel();
        }

        await Task.WhenAll(sessions.Values.Select(registration => registration.RunTask));
    }

    internal async Task<SessionCycleResult?> RunSessionCycleAsync(
        ExchangeSyncAccountDescriptor account,
        CancellationToken cancellationToken = default)
    {
        var bootstrap = await TryBootstrapAsync(account, cancellationToken);

        if (bootstrap is null)
        {
            return null;
        }

        var listenKey = await privateRestClient.StartListenKeyAsync(bootstrap.ApiKey, cancellationToken);
        var listenKeyStartedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var seedSnapshot = await privateRestClient.GetAccountSnapshotAsync(
            account.ExchangeAccountId,
            account.OwnerUserId,
            account.ExchangeName,
            bootstrap.ApiKey,
            bootstrap.ApiSecret,
            cancellationToken);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keepAliveFailure = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var keepAliveTask = RunKeepAliveLoopAsync(
            bootstrap.ApiKey,
            account,
            keepAliveFailure,
            linkedCancellation);
        var currentState = new ExchangeAccountSnapshotState(seedSnapshot);

        try
        {
            snapshotHub.Publish(seedSnapshot);

            await UpdateConnectionStateAsync(
                account,
                ExchangePrivateStreamConnectionState.Connected,
                errorCode: null,
                consecutiveFailureCount: 0,
                listenKeyStartedAtUtc: listenKeyStartedAtUtc,
                lastPrivateStreamEventAtUtc: seedSnapshot.ObservedAtUtc,
                cancellationToken: cancellationToken);

            await foreach (var streamEvent in privateStreamClient.StreamAsync(listenKey, linkedCancellation.Token))
            {
                if (string.Equals(streamEvent.EventType, "listenKeyExpired", StringComparison.Ordinal))
                {
                    return new SessionCycleResult(
                        ShouldReconnect: true,
                        ConnectionState: ExchangePrivateStreamConnectionState.ListenKeyExpired,
                        ErrorCode: "ListenKeyExpired");
                }

                currentState.Apply(streamEvent);
                await ApplyExecutionOrderUpdatesAsync(streamEvent.OrderUpdates, cancellationToken);

                var snapshot = currentState.CreateSnapshot(
                    account.ExchangeAccountId,
                    account.OwnerUserId,
                    account.ExchangeName,
                    timeProvider.GetUtcNow().UtcDateTime,
                    "Binance.PrivateStream.AccountUpdate");

                snapshotHub.Publish(snapshot);

                await UpdateConnectionStateAsync(
                    account,
                    ExchangePrivateStreamConnectionState.Connected,
                    errorCode: null,
                    consecutiveFailureCount: 0,
                    lastPrivateStreamEventAtUtc: streamEvent.EventTimeUtc,
                    cancellationToken: cancellationToken);
            }

            if (keepAliveFailure.Task.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(keepAliveFailure.Task.Result))
            {
                return new SessionCycleResult(
                    ShouldReconnect: true,
                    ConnectionState: ExchangePrivateStreamConnectionState.Reconnecting,
                    ErrorCode: keepAliveFailure.Task.Result);
            }

            return new SessionCycleResult(
                ShouldReconnect: true,
                ConnectionState: ExchangePrivateStreamConnectionState.Reconnecting,
                ErrorCode: "StreamDisconnected");
        }
        finally
        {
            linkedCancellation.Cancel();

            try
            {
                await keepAliveTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
            }

            try
            {
                await privateRestClient.CloseListenKeyAsync(bootstrap.ApiKey, cancellationToken);
            }
            catch
            {
            }
        }
    }

    private async Task RefreshSessionsAsync(CancellationToken cancellationToken)
    {
        foreach (var completedSession in sessions
                     .Where(pair => pair.Value.RunTask.IsCompleted)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            sessions.Remove(completedSession);
        }

        var trackedAccounts = await ListTrackedAccountsAsync(cancellationToken);
        var trackedAccountIds = trackedAccounts
            .Select(account => account.ExchangeAccountId)
            .ToHashSet();

        foreach (var staleSession in sessions
                     .Where(pair => !trackedAccountIds.Contains(pair.Key))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            sessions[staleSession].CancellationTokenSource.Cancel();
            sessions.Remove(staleSession);
        }

        foreach (var account in trackedAccounts)
        {
            if (sessions.ContainsKey(account.ExchangeAccountId))
            {
                continue;
            }

            var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var runTask = RunManagedSessionAsync(account, cancellationTokenSource.Token);

            sessions[account.ExchangeAccountId] = new SessionRegistration(cancellationTokenSource, runTask);
        }
    }

    private async Task RunManagedSessionAsync(ExchangeSyncAccountDescriptor account, CancellationToken cancellationToken)
    {
        var consecutiveFailureCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            SessionCycleResult? cycleResult = null;

            try
            {
                await UpdateConnectionStateAsync(
                    account,
                    ExchangePrivateStreamConnectionState.Connecting,
                    errorCode: null,
                    consecutiveFailureCount: consecutiveFailureCount,
                    cancellationToken: cancellationToken);

                cycleResult = await RunSessionCycleAsync(account, cancellationToken);

                if (cycleResult is null)
                {
                    return;
                }

                consecutiveFailureCount = cycleResult.ShouldReconnect
                    ? consecutiveFailureCount + 1
                    : 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                cycleResult = new SessionCycleResult(
                    ShouldReconnect: true,
                    ConnectionState: ExchangePrivateStreamConnectionState.Reconnecting,
                    ErrorCode: "StreamCycleFailed");
                consecutiveFailureCount++;
            }

            await UpdateConnectionStateAsync(
                account,
                cycleResult.ConnectionState,
                cycleResult.ErrorCode,
                consecutiveFailureCount,
                cancellationToken: cancellationToken);

            if (!cycleResult.ShouldReconnect)
            {
                return;
            }

            logger.LogInformation(
                "Binance private stream reconnect scheduled for account {ExchangeAccountId}. Reason={ReasonCode}.",
                account.ExchangeAccountId,
                cycleResult.ErrorCode);

            await Task.Delay(TimeSpan.FromSeconds(optionsValue.ReconnectDelaySeconds), cancellationToken);
        }

        await UpdateConnectionStateAsync(
            account,
            ExchangePrivateStreamConnectionState.Disconnected,
            errorCode: null,
            cancellationToken: CancellationToken.None);
    }

    private async Task<BootstrapResult?> TryBootstrapAsync(
        ExchangeSyncAccountDescriptor account,
        CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        using var systemScope = scope.ServiceProvider
            .GetRequiredService<IDataScopeContextAccessor>()
            .BeginScope(hasIsolationBypass: true);
        var credentialService = scope.ServiceProvider.GetRequiredService<IExchangeCredentialService>();

        try
        {
            var credentialAccess = await credentialService.GetAsync(
                new ExchangeCredentialAccessRequest(
                    account.ExchangeAccountId,
                    SystemActor,
                    ExchangeCredentialAccessPurpose.Synchronization),
                cancellationToken);
            return new BootstrapResult(credentialAccess.ApiKey, credentialAccess.ApiSecret);
        }
        catch (InvalidOperationException)
        {
            await scope.ServiceProvider
                .GetRequiredService<ExchangeAccountSyncStateService>()
                .RecordConnectionStateAsync(
                    account,
                    ExchangePrivateStreamConnectionState.Disconnected,
                    errorCode: "CredentialAccessBlocked",
                    consecutiveFailureCount: 0,
                    cancellationToken: cancellationToken);

            logger.LogInformation(
                "Binance private stream skipped for account {ExchangeAccountId} because synchronization access is blocked.",
                account.ExchangeAccountId);

            return null;
        }
    }

    private async Task RunKeepAliveLoopAsync(
        string apiKey,
        ExchangeSyncAccountDescriptor account,
        TaskCompletionSource<string?> keepAliveFailure,
        CancellationTokenSource linkedCancellation)
    {
        while (!linkedCancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(optionsValue.ListenKeyRenewalIntervalMinutes), linkedCancellation.Token);
                await privateRestClient.KeepAliveListenKeyAsync(apiKey, linkedCancellation.Token);

                await UpdateConnectionStateAsync(
                    account,
                    ExchangePrivateStreamConnectionState.Connected,
                    errorCode: null,
                    consecutiveFailureCount: 0,
                    listenKeyRenewedAtUtc: timeProvider.GetUtcNow().UtcDateTime,
                    cancellationToken: linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                keepAliveFailure.TrySetResult("ListenKeyKeepAliveFailed");
                linkedCancellation.Cancel();
                break;
            }
        }
    }

    private async Task<IReadOnlyCollection<ExchangeSyncAccountDescriptor>> ListTrackedAccountsAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        using var systemScope = scope.ServiceProvider
            .GetRequiredService<IDataScopeContextAccessor>()
            .BeginScope(hasIsolationBypass: true);
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await dbContext.ExchangeAccounts
            .AsNoTracking()
            .Where(entity => !entity.IsDeleted &&
                             entity.ExchangeName == "Binance" &&
                             entity.ApiKeyCiphertext != null &&
                             entity.ApiSecretCiphertext != null)
            .Select(entity => new ExchangeSyncAccountDescriptor(
                entity.Id,
                entity.OwnerUserId,
                entity.ExchangeName))
            .ToListAsync(cancellationToken);
    }

    private async Task UpdateConnectionStateAsync(
        ExchangeSyncAccountDescriptor account,
        ExchangePrivateStreamConnectionState connectionState,
        string? errorCode,
        int? consecutiveFailureCount = null,
        DateTime? listenKeyStartedAtUtc = null,
        DateTime? listenKeyRenewedAtUtc = null,
        DateTime? lastPrivateStreamEventAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = serviceScopeFactory.CreateScope();
        using var systemScope = scope.ServiceProvider
            .GetRequiredService<IDataScopeContextAccessor>()
            .BeginScope(hasIsolationBypass: true);
        var syncStateService = scope.ServiceProvider.GetRequiredService<ExchangeAccountSyncStateService>();

        await syncStateService.RecordConnectionStateAsync(
            account,
            connectionState,
            errorCode,
            consecutiveFailureCount,
            listenKeyStartedAtUtc,
            listenKeyRenewedAtUtc,
            lastPrivateStreamEventAtUtc,
            cancellationToken);
    }

    private async Task ApplyExecutionOrderUpdatesAsync(
        IReadOnlyCollection<BinanceOrderStatusSnapshot> orderUpdates,
        CancellationToken cancellationToken)
    {
        if (orderUpdates.Count == 0)
        {
            return;
        }

        using var scope = serviceScopeFactory.CreateScope();
        using var systemScope = scope.ServiceProvider
            .GetRequiredService<IDataScopeContextAccessor>()
            .BeginScope(hasIsolationBypass: true);
        var lifecycleService = scope.ServiceProvider.GetRequiredService<ExecutionOrderLifecycleService>();

        foreach (var orderUpdate in orderUpdates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await lifecycleService.ApplyExchangeUpdateAsync(orderUpdate, cancellationToken);
        }
    }

    private sealed class ExchangeAccountSnapshotState(ExchangeAccountSnapshot snapshot)
    {
        private readonly Dictionary<string, ExchangeBalanceSnapshot> balances = snapshot.Balances.ToDictionary(
            balance => NormalizeCode(balance.Asset),
            StringComparer.Ordinal);
        private readonly Dictionary<string, ExchangePositionSnapshot> positions = snapshot.Positions.ToDictionary(
            position => CreatePositionKey(position.Symbol, position.PositionSide),
            StringComparer.Ordinal);
        private DateTime observedAtUtc = NormalizeTimestamp(snapshot.ObservedAtUtc);

        public void Apply(BinancePrivateStreamEvent streamEvent)
        {
            observedAtUtc = NormalizeTimestamp(streamEvent.EventTimeUtc);

            foreach (var balanceUpdate in streamEvent.BalanceUpdates)
            {
                var asset = NormalizeCode(balanceUpdate.Asset);

                if (IsEmptyBalance(balanceUpdate))
                {
                    balances.Remove(asset);
                    continue;
                }

                balances.TryGetValue(asset, out var existingBalance);
                balances[asset] = balanceUpdate with
                {
                    Asset = asset,
                    AvailableBalance = balanceUpdate.AvailableBalance ?? existingBalance?.AvailableBalance,
                    MaxWithdrawAmount = balanceUpdate.MaxWithdrawAmount ?? existingBalance?.MaxWithdrawAmount,
                    ExchangeUpdatedAtUtc = NormalizeTimestamp(balanceUpdate.ExchangeUpdatedAtUtc)
                };
            }

            foreach (var positionUpdate in streamEvent.PositionUpdates)
            {
                var key = CreatePositionKey(positionUpdate.Symbol, positionUpdate.PositionSide);

                if (IsFlatPosition(positionUpdate))
                {
                    positions.Remove(key);
                    continue;
                }

                positions[key] = positionUpdate with
                {
                    Symbol = NormalizeCode(positionUpdate.Symbol),
                    PositionSide = NormalizeCode(positionUpdate.PositionSide),
                    MarginType = NormalizeMarginType(positionUpdate.MarginType),
                    ExchangeUpdatedAtUtc = NormalizeTimestamp(positionUpdate.ExchangeUpdatedAtUtc)
                };
            }
        }

        public ExchangeAccountSnapshot CreateSnapshot(
            Guid exchangeAccountId,
            string ownerUserId,
            string exchangeName,
            DateTime receivedAtUtc,
            string source)
        {
            return new ExchangeAccountSnapshot(
                exchangeAccountId,
                ownerUserId.Trim(),
                exchangeName.Trim(),
                balances.Values
                    .OrderBy(balance => balance.Asset, StringComparer.Ordinal)
                    .ToArray(),
                positions.Values
                    .OrderBy(position => position.Symbol, StringComparer.Ordinal)
                    .ThenBy(position => position.PositionSide, StringComparer.Ordinal)
                    .ToArray(),
                observedAtUtc,
                NormalizeTimestamp(receivedAtUtc),
                source);
        }

        private static bool IsEmptyBalance(ExchangeBalanceSnapshot snapshot)
        {
            return snapshot.WalletBalance == 0m &&
                   snapshot.CrossWalletBalance == 0m &&
                   (snapshot.AvailableBalance ?? 0m) == 0m &&
                   (snapshot.MaxWithdrawAmount ?? 0m) == 0m;
        }

        private static bool IsFlatPosition(ExchangePositionSnapshot snapshot)
        {
            return snapshot.Quantity == 0m &&
                   snapshot.EntryPrice == 0m &&
                   snapshot.BreakEvenPrice == 0m &&
                   snapshot.UnrealizedProfit == 0m &&
                   snapshot.IsolatedWallet == 0m;
        }

        private static string CreatePositionKey(string symbol, string positionSide)
        {
            return $"{NormalizeCode(symbol)}:{NormalizeCode(positionSide)}";
        }

        private static string NormalizeCode(string value)
        {
            return value.Trim().ToUpperInvariant();
        }

        private static string NormalizeMarginType(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "cross"
                : value.Trim().ToLowerInvariant();
        }

        private static DateTime NormalizeTimestamp(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };
        }
    }

    internal sealed record SessionCycleResult(
        bool ShouldReconnect,
        ExchangePrivateStreamConnectionState ConnectionState,
        string ErrorCode);

    private sealed record BootstrapResult(string ApiKey, string ApiSecret);

    private sealed record SessionRegistration(
        CancellationTokenSource CancellationTokenSource,
        Task RunTask);
}
