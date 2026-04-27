using System.Globalization;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.DemoPortfolio;

public sealed class DemoSessionService(
    ApplicationDbContext dbContext,
    DemoConsistencyWatchdogService demoConsistencyWatchdogService,
    DemoWalletValuationService demoWalletValuationService,
    IAuditLogService auditLogService,
    IOptions<DemoSessionOptions> options,
    TimeProvider timeProvider,
    ILogger<DemoSessionService> logger) : IDemoSessionService
{
    private const string SystemActor = "system:demo-session";
    private const string PortfolioScopeKey = "portfolio";
    private readonly DemoSessionOptions optionsValue = options.Value;
    private static readonly ExecutionOrderState[] OpenOrderStates =
    [
        ExecutionOrderState.Received,
        ExecutionOrderState.GatePassed,
        ExecutionOrderState.Dispatching,
        ExecutionOrderState.Submitted,
        ExecutionOrderState.PartiallyFilled,
        ExecutionOrderState.CancelRequested
    ];

    public async Task<DemoSessionSnapshot?> GetActiveSessionAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        var session = await ResolveActiveSessionAsync(NormalizeRequired(ownerUserId, nameof(ownerUserId)), createIfMissing: false, cancellationToken);
        return session is null ? null : MapSnapshot(session);
    }

    public async Task<DemoSessionSnapshot> EnsureActiveSessionAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        var session = await ResolveActiveSessionAsync(NormalizeRequired(ownerUserId, nameof(ownerUserId)), createIfMissing: true, cancellationToken)
            ?? throw new InvalidOperationException("Demo session could not be established.");
        return MapSnapshot(session);
    }

    public async Task<DemoSessionSnapshot?> RunConsistencyCheckAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        var session = await ResolveActiveSessionAsync(NormalizeRequired(ownerUserId, nameof(ownerUserId)), createIfMissing: false, cancellationToken);

        if (session is null)
        {
            return null;
        }

        await EnsureSeededPortfolioStateAsync(session, cancellationToken);
        var missingProjectionRecovery = await RehydrateMissingPositionProjectionsAsync(session, cancellationToken);
        var leakReconciliation = await ReconcileFailedDemoOrderPositionLeaksAsync(session, cancellationToken);
        var walletLeakReconciliation = await ReconcileFailedDemoOrderWalletLeaksAsync(session, cancellationToken);
        var botStateRefreshes = leakReconciliation.OpenPositionCountsByBotId
            .ToDictionary(item => item.Key, item => (int?)item.Value);

        foreach (var botId in missingProjectionRecovery.AffectedBotIds)
        {
            botStateRefreshes.TryAdd(botId, null);
        }

        foreach (var staleBotId in await ResolveStaleDemoBotStateIdsAsync(session.OwnerUserId, cancellationToken))
        {
            botStateRefreshes.TryAdd(staleBotId, null);
        }

        if (botStateRefreshes.Count > 0 || walletLeakReconciliation.ReconciledOrderCount > 0)
        {
            foreach (var refresh in botStateRefreshes)
            {
                await UpdateBotStateAsync(refresh.Key, cancellationToken, refresh.Value);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (leakReconciliation.ReconciledPositionCount > 0)
        {
            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    SystemActor,
                    "DemoSession.FailedOrderLeakReconciled",
                    $"DemoSession/{session.Id}",
                    BuildFailedOrderLeakReconciliationContext(leakReconciliation.ReconciledPositionCount, leakReconciliation.SampleExecutionOrderId),
                    CorrelationId: null,
                    Outcome: "Applied",
                    Environment: nameof(ExecutionEnvironment.Demo)),
                cancellationToken);
        }

        if (walletLeakReconciliation.ReconciledOrderCount > 0)
        {
            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    SystemActor,
                    "DemoSession.FailedOrderWalletLeakReconciled",
                    $"DemoSession/{session.Id}",
                    BuildFailedOrderWalletLeakReconciliationContext(walletLeakReconciliation.ReconciledOrderCount, walletLeakReconciliation.SampleExecutionOrderId),
                    CorrelationId: null,
                    Outcome: "Applied",
                    Environment: nameof(ExecutionEnvironment.Demo)),
                cancellationToken);
        }

        if (missingProjectionRecovery.RehydratedPositionCount > 0)
        {
            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    SystemActor,
                    "DemoSession.MissingPositionProjectionRehydrated",
                    $"DemoSession/{session.Id}",
                    BuildMissingPositionProjectionRecoveryContext(missingProjectionRecovery.RehydratedPositionCount, missingProjectionRecovery.SamplePositionKey),
                    CorrelationId: null,
                    Outcome: "Applied",
                    Environment: nameof(ExecutionEnvironment.Demo)),
                cancellationToken);
        }

        var previousStatus = session.ConsistencyStatus;
        var result = await demoConsistencyWatchdogService.EvaluateAsync(session, cancellationToken);
        ApplyConsistency(session, result);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (result.Status == DemoConsistencyStatus.DriftDetected &&
            previousStatus != DemoConsistencyStatus.DriftDetected)
        {
            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    SystemActor,
                    "DemoSession.DriftDetected",
                    $"DemoSession/{session.Id}",
                    result.Summary,
                    CorrelationId: null,
                    Outcome: "Detected",
                    Environment: nameof(ExecutionEnvironment.Demo)),
                cancellationToken);
        }

        return MapSnapshot(session);
    }

    private async Task<MissingPositionProjectionRecoveryResult> RehydrateMissingPositionProjectionsAsync(
        DemoSession session,
        CancellationToken cancellationToken)
    {
        var sessionStartedAtUtc = NormalizeTimestamp(session.StartedAtUtc);
        var transactions = await dbContext.DemoLedgerTransactions
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted &&
                entity.CreatedDate >= sessionStartedAtUtc &&
                entity.PositionQuantityAfter != null &&
                entity.Symbol != null)
            .ToListAsync(cancellationToken);

        if (transactions.Count == 0)
        {
            return MissingPositionProjectionRecoveryResult.Empty;
        }

        var latestTransactions = transactions
            .Where(IsRecoverablePositionSnapshot)
            .GroupBy(entity => CreatePositionKey(entity.PositionScopeKey, entity.Symbol!), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(entity => NormalizeTimestamp(entity.CreatedDate))
                .ThenByDescending(entity => NormalizeTimestamp(entity.OccurredAtUtc))
                .ThenByDescending(entity => entity.Id)
                .First())
            .Where(entity => Math.Abs(entity.PositionQuantityAfter!.Value) > optionsValue.ConsistencyTolerance)
            .ToList();

        if (latestTransactions.Count == 0)
        {
            return MissingPositionProjectionRecoveryResult.Empty;
        }

        var existingPositions = (await dbContext.DemoPositions
                .IgnoreQueryFilters()
                .Where(entity => entity.OwnerUserId == session.OwnerUserId)
                .ToListAsync(cancellationToken))
            .ToDictionary(
                entity => CreatePositionKey(entity.PositionScopeKey, entity.Symbol),
                StringComparer.Ordinal);

        var affectedBotIds = new HashSet<Guid>();
        var rehydratedPositionCount = 0;
        string? samplePositionKey = null;

        foreach (var transaction in latestTransactions)
        {
            var positionKey = CreatePositionKey(transaction.PositionScopeKey, transaction.Symbol!);
            if (existingPositions.TryGetValue(positionKey, out var existingPosition))
            {
                if (!existingPosition.IsDeleted)
                {
                    continue;
                }

                ApplyPositionSnapshot(existingPosition, transaction);
                existingPosition.IsDeleted = false;
            }
            else
            {
                var position = CreatePositionFromLedgerSnapshot(session.OwnerUserId, transaction);
                dbContext.DemoPositions.Add(position);
                existingPositions[positionKey] = position;
            }

            rehydratedPositionCount++;
            samplePositionKey ??= positionKey;

            if (transaction.BotId.HasValue)
            {
                affectedBotIds.Add(transaction.BotId.Value);
            }
        }

        if (rehydratedPositionCount == 0)
        {
            return MissingPositionProjectionRecoveryResult.Empty;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new MissingPositionProjectionRecoveryResult(rehydratedPositionCount, affectedBotIds, samplePositionKey);
    }

    private async Task<FailedOrderLeakReconciliationResult> ReconcileFailedDemoOrderPositionLeaksAsync(
        DemoSession session,
        CancellationToken cancellationToken)
    {
        var sessionStartedAtUtc = NormalizeTimestamp(session.StartedAtUtc);
        var positions = await dbContext.DemoPositions
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted &&
                entity.BotId != null &&
                entity.Quantity != 0m)
            .ToListAsync(cancellationToken);

        if (positions.Count == 0)
        {
            return FailedOrderLeakReconciliationResult.Empty;
        }

        var positionTransactions = await dbContext.DemoLedgerTransactions
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted &&
                entity.CreatedDate >= sessionStartedAtUtc &&
                entity.BotId != null &&
                entity.Symbol != null &&
                entity.OrderId != null &&
                entity.PositionQuantityAfter != null &&
                entity.PositionQuantityAfter != 0m)
            .ToListAsync(cancellationToken);

        if (positionTransactions.Count == 0)
        {
            return FailedOrderLeakReconciliationResult.Empty;
        }

        var failedOrders = (await dbContext.ExecutionOrders
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == session.OwnerUserId &&
                    !entity.IsDeleted &&
                    entity.ExecutionEnvironment == ExecutionEnvironment.Demo &&
                    entity.ExecutorKind == ExecutionOrderExecutorKind.Virtual &&
                    entity.CreatedDate >= sessionStartedAtUtc &&
                    (entity.State == ExecutionOrderState.Failed ||
                     entity.State == ExecutionOrderState.Rejected) &&
                    entity.FailureCode == "VirtualWatchdogFailedClosed" &&
                    (entity.FilledQuantity > 0m ||
                     entity.LastFilledAtUtc != null))
                .ToListAsync(cancellationToken))
            .ToDictionary(entity => entity.Id.ToString("N"), StringComparer.Ordinal);

        if (failedOrders.Count == 0)
        {
            return FailedOrderLeakReconciliationResult.Empty;
        }

        var affectedBotIds = new HashSet<Guid>();
        var reconciledPositionCount = 0;
        Guid? sampleExecutionOrderId = null;
        var now = NormalizeTimestamp(timeProvider.GetUtcNow().UtcDateTime);

        foreach (var position in positions)
        {
            var latestFillTransaction = positionTransactions
                .Where(entity =>
                    entity.BotId == position.BotId &&
                    string.Equals(entity.PositionScopeKey, position.PositionScopeKey, StringComparison.Ordinal) &&
                    string.Equals(entity.Symbol, position.Symbol, StringComparison.Ordinal))
                .OrderByDescending(entity => NormalizeTimestamp(entity.CreatedDate))
                .ThenByDescending(entity => NormalizeTimestamp(entity.OccurredAtUtc))
                .ThenByDescending(entity => entity.Id)
                .FirstOrDefault();

            if (latestFillTransaction?.OrderId is null ||
                !failedOrders.TryGetValue(NormalizeOrderId(latestFillTransaction.OrderId), out var failedOrder))
            {
                continue;
            }

            AddReconciliationTransaction(session.OwnerUserId, position, failedOrder.Id, now);
            ClearFailedOrderFillProjection(failedOrder, now);
            ClearPosition(position, now);
            affectedBotIds.Add(position.BotId!.Value);
            reconciledPositionCount++;
            sampleExecutionOrderId ??= failedOrder.Id;
        }

        var openPositionCountsByBotId = affectedBotIds.ToDictionary(
            botId => botId,
            botId => positions.Count(position => position.BotId == botId && !position.IsDeleted && position.Quantity != 0m));

        return reconciledPositionCount == 0
            ? FailedOrderLeakReconciliationResult.Empty
            : new FailedOrderLeakReconciliationResult(reconciledPositionCount, openPositionCountsByBotId, sampleExecutionOrderId);
    }

    private async Task<FailedOrderWalletLeakReconciliationResult> ReconcileFailedDemoOrderWalletLeaksAsync(
        DemoSession session,
        CancellationToken cancellationToken)
    {
        var sessionStartedAtUtc = NormalizeTimestamp(session.StartedAtUtc);
        var failedOrders = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted &&
                entity.ExecutionEnvironment == ExecutionEnvironment.Demo &&
                entity.ExecutorKind == ExecutionOrderExecutorKind.Virtual &&
                entity.CreatedDate >= sessionStartedAtUtc &&
                (entity.State == ExecutionOrderState.Failed ||
                 entity.State == ExecutionOrderState.Rejected) &&
                entity.FailureCode == "VirtualWatchdogFailedClosed")
            .Select(entity => new { entity.Id })
            .ToListAsync(cancellationToken);

        if (failedOrders.Count == 0)
        {
            return FailedOrderWalletLeakReconciliationResult.Empty;
        }

        var failedOrderIds = failedOrders
            .Select(entity => entity.Id.ToString("N"))
            .ToArray();
        var existingReconciliationOrderIds = await dbContext.DemoLedgerTransactions
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted &&
                entity.TransactionType == DemoLedgerTransactionType.Reconciled &&
                entity.OperationId.StartsWith("demo-reconcile:failed-order-wallet:") &&
                entity.OrderId != null &&
                failedOrderIds.Contains(entity.OrderId))
            .Select(entity => entity.OrderId!)
            .ToListAsync(cancellationToken);
        var reconciledOrderIds = existingReconciliationOrderIds.ToHashSet(StringComparer.Ordinal);
        var ledgerDeltas = await (
                from transaction in dbContext.DemoLedgerTransactions.IgnoreQueryFilters().AsNoTracking()
                join entry in dbContext.DemoLedgerEntries.IgnoreQueryFilters().AsNoTracking()
                    on transaction.Id equals entry.DemoLedgerTransactionId
                where transaction.OwnerUserId == session.OwnerUserId &&
                      !transaction.IsDeleted &&
                      transaction.CreatedDate >= sessionStartedAtUtc &&
                      transaction.OrderId != null &&
                      failedOrderIds.Contains(transaction.OrderId) &&
                      transaction.TransactionType != DemoLedgerTransactionType.Reconciled &&
                      !entry.IsDeleted
                select new FailedOrderWalletLedgerDelta(
                    transaction.OrderId!,
                    entry.Asset,
                    entry.AvailableDelta,
                    entry.ReservedDelta))
            .ToListAsync(cancellationToken);

        if (ledgerDeltas.Count == 0)
        {
            return FailedOrderWalletLeakReconciliationResult.Empty;
        }

        var wallets = await dbContext.DemoWallets
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted)
            .ToDictionaryAsync(entity => entity.Asset, StringComparer.Ordinal, cancellationToken);
        var now = NormalizeTimestamp(timeProvider.GetUtcNow().UtcDateTime);
        var reconciledOrderCount = 0;
        Guid? sampleExecutionOrderId = null;

        foreach (var failedOrder in failedOrders)
        {
            var orderId = failedOrder.Id.ToString("N");

            if (reconciledOrderIds.Contains(orderId))
            {
                continue;
            }

            var deltas = ledgerDeltas
                .Where(entity => string.Equals(entity.OrderId, orderId, StringComparison.Ordinal))
                .GroupBy(entity => entity.Asset, StringComparer.Ordinal)
                .Select(group => new
                {
                    Asset = group.Key,
                    AvailableDelta = group.Sum(entity => entity.AvailableDelta),
                    ReservedDelta = group.Sum(entity => entity.ReservedDelta)
                })
                .Where(entity =>
                    Math.Abs(entity.AvailableDelta) > optionsValue.ConsistencyTolerance ||
                    Math.Abs(entity.ReservedDelta) > optionsValue.ConsistencyTolerance)
                .ToArray();

            if (deltas.Length == 0)
            {
                continue;
            }

            var transactionId = Guid.NewGuid();
            dbContext.DemoLedgerTransactions.Add(new DemoLedgerTransaction
            {
                Id = transactionId,
                OwnerUserId = session.OwnerUserId,
                OperationId = $"demo-reconcile:failed-order-wallet:{orderId}",
                TransactionType = DemoLedgerTransactionType.Reconciled,
                PositionScopeKey = PortfolioScopeKey,
                OrderId = orderId,
                OccurredAtUtc = now
            });

            foreach (var delta in deltas)
            {
                if (!wallets.TryGetValue(delta.Asset, out var wallet))
                {
                    wallet = new DemoWallet
                    {
                        OwnerUserId = session.OwnerUserId,
                        Asset = delta.Asset
                    };
                    wallets[delta.Asset] = wallet;
                    dbContext.DemoWallets.Add(wallet);
                }

                wallet.AvailableBalance = ClampToZero(wallet.AvailableBalance - delta.AvailableDelta);
                wallet.ReservedBalance = ClampToZero(wallet.ReservedBalance - delta.ReservedDelta);
                wallet.LastActivityAtUtc = now;
                dbContext.DemoLedgerEntries.Add(new DemoLedgerEntry
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = session.OwnerUserId,
                    DemoLedgerTransactionId = transactionId,
                    Asset = delta.Asset,
                    AvailableDelta = ClampToZero(-delta.AvailableDelta),
                    ReservedDelta = ClampToZero(-delta.ReservedDelta),
                    AvailableBalanceAfter = wallet.AvailableBalance,
                    ReservedBalanceAfter = wallet.ReservedBalance
                });
            }

            await demoWalletValuationService.SyncAsync(wallets.Values, cancellationToken);
            reconciledOrderCount++;
            sampleExecutionOrderId ??= failedOrder.Id;
        }

        return reconciledOrderCount == 0
            ? FailedOrderWalletLeakReconciliationResult.Empty
            : new FailedOrderWalletLeakReconciliationResult(reconciledOrderCount, sampleExecutionOrderId);
    }

    private async Task<HashSet<Guid>> ResolveStaleDemoBotStateIdsAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        var bots = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == ownerUserId && !entity.IsDeleted)
            .Select(entity => new
            {
                entity.Id,
                entity.OpenOrderCount,
                entity.OpenPositionCount
            })
            .ToListAsync(cancellationToken);

        if (bots.Count == 0)
        {
            return [];
        }

        var botIds = bots.Select(entity => entity.Id).ToArray();
        var openPositionCounts = await dbContext.DemoPositions
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.BotId.HasValue &&
                botIds.Contains(entity.BotId.Value) &&
                !entity.IsDeleted &&
                entity.Quantity != 0m)
            .GroupBy(entity => entity.BotId!.Value)
            .Select(group => new { BotId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entity => entity.BotId, entity => entity.Count, cancellationToken);
        var openOrderCounts = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.BotId.HasValue &&
                botIds.Contains(entity.BotId.Value) &&
                !entity.IsDeleted &&
                OpenOrderStates.Contains(entity.State))
            .GroupBy(entity => entity.BotId!.Value)
            .Select(group => new { BotId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entity => entity.BotId, entity => entity.Count, cancellationToken);

        return bots
            .Where(entity =>
                entity.OpenPositionCount != openPositionCounts.GetValueOrDefault(entity.Id) ||
                entity.OpenOrderCount != openOrderCounts.GetValueOrDefault(entity.Id))
            .Select(entity => entity.Id)
            .ToHashSet();
    }

    private void AddReconciliationTransaction(
        string ownerUserId,
        DemoPosition position,
        Guid executionOrderId,
        DateTime occurredAtUtc)
    {
        dbContext.DemoLedgerTransactions.Add(new DemoLedgerTransaction
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            OperationId = $"demo-reconcile:failed-order-leak:{position.Id:N}:{executionOrderId:N}",
            TransactionType = DemoLedgerTransactionType.Reconciled,
            BotId = position.BotId,
            PositionScopeKey = position.PositionScopeKey,
            OrderId = executionOrderId.ToString("N"),
            Symbol = position.Symbol,
            BaseAsset = position.BaseAsset,
            QuoteAsset = position.QuoteAsset,
            PositionKind = DemoPositionKind.Spot,
            PositionQuantityAfter = 0m,
            PositionCostBasisAfter = 0m,
            PositionAverageEntryPriceAfter = 0m,
            CumulativeRealizedPnlAfter = 0m,
            UnrealizedPnlAfter = 0m,
            CumulativeFeesInQuoteAfter = 0m,
            NetFundingInQuoteAfter = 0m,
            OccurredAtUtc = occurredAtUtc
        });
    }

    private static void ClearFailedOrderFillProjection(ExecutionOrder order, DateTime observedAtUtc)
    {
        order.FilledQuantity = 0m;
        order.AverageFillPrice = null;
        order.LastFilledAtUtc = null;
        order.UpdatedDate = observedAtUtc;
    }

    private static void ClearPosition(DemoPosition position, DateTime observedAtUtc)
    {
        position.PositionKind = DemoPositionKind.Spot;
        position.MarginMode = null;
        position.Leverage = null;
        position.Quantity = 0m;
        position.CostBasis = 0m;
        position.AverageEntryPrice = 0m;
        position.RealizedPnl = 0m;
        position.UnrealizedPnl = 0m;
        position.TotalFeesInQuote = 0m;
        position.NetFundingInQuote = 0m;
        position.IsolatedMargin = null;
        position.MaintenanceMarginRate = null;
        position.MaintenanceMargin = null;
        position.MarginBalance = null;
        position.LiquidationPrice = null;
        position.LastMarkPrice = null;
        position.LastPrice = null;
        position.LastFillPrice = null;
        position.LastFundingRate = null;
        position.LastFilledAtUtc = null;
        position.LastValuationAtUtc = observedAtUtc;
        position.LastFundingAppliedAtUtc = null;
    }

    private static string NormalizeOrderId(string value) => value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    public async Task<DemoSessionSnapshot> ResetAsync(DemoSessionResetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureDemoEnvironment(request.Environment);
        var ownerUserId = NormalizeRequired(request.OwnerUserId, nameof(request.OwnerUserId));
        var actor = NormalizeRequired(request.Actor, nameof(request.Actor), 256);
        var resetReason = NormalizeOptional(request.Reason, 256);
        var now = NormalizeTimestamp(timeProvider.GetUtcNow().UtcDateTime);
        var activeSession = await ResolveActiveSessionAsync(ownerUserId, createIfMissing: false, cancellationToken);
        var nextSequenceNumber = activeSession?.SequenceNumber + 1 ?? await ResolveNextSequenceNumberAsync(ownerUserId, cancellationToken);
        var seedAsset = NormalizeAsset(request.SeedAsset ?? optionsValue.DefaultSeedAsset);
        var seedAmount = ValidatePositiveAmount(request.SeedAmount ?? optionsValue.DefaultSeedAmount);
        var affectedBotIds = new HashSet<Guid>();
        var terminalizedOrderCount = 0;

        if (activeSession is not null)
        {
            activeSession.State = DemoSessionState.Closed;
            activeSession.ClosedAtUtc = now;
            terminalizedOrderCount = await TerminalizeOpenOrdersAsync(
                ownerUserId,
                activeSession.StartedAtUtc,
                resetReason,
                affectedBotIds,
                now,
                cancellationToken);
        }

        await ResetWalletsAsync(ownerUserId, now, cancellationToken);
        await ResetPositionsAsync(ownerUserId, now, affectedBotIds, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var botId in affectedBotIds)
        {
            await UpdateBotStateAsync(botId, cancellationToken);
        }

        var session = new DemoSession
        {
            OwnerUserId = ownerUserId,
            SequenceNumber = nextSequenceNumber,
            SeedAsset = seedAsset,
            SeedAmount = seedAmount,
            StartedAtUtc = now,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.Unknown
        };

        dbContext.DemoSessions.Add(session);
        await SeedResetWalletAsync(session, seedAsset, seedAmount, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        var consistencyResult = await demoConsistencyWatchdogService.EvaluateAsync(session, cancellationToken);
        ApplyConsistency(session, consistencyResult);
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                actor,
                "DemoSession.ResetApplied",
                $"DemoSession/{session.Id}",
                BuildResetContext(session.SequenceNumber, seedAsset, seedAmount, activeSession?.Id, terminalizedOrderCount, resetReason, consistencyResult.Summary),
                request.CorrelationId,
                "Applied",
                nameof(ExecutionEnvironment.Demo)),
            cancellationToken);

        logger.LogInformation("Demo session reset completed for session {DemoSessionId}.", session.Id);
        return MapSnapshot(session);
    }

    private async Task<DemoSession?> ResolveActiveSessionAsync(string ownerUserId, bool createIfMissing, CancellationToken cancellationToken)
    {
        var session = await dbContext.DemoSessions
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.OwnerUserId == ownerUserId &&
                          !entity.IsDeleted &&
                          entity.State == DemoSessionState.Active,
                cancellationToken);

        if (session is not null)
        {
            return session;
        }

        var hasCurrentState = await HasCurrentDemoStateAsync(ownerUserId, cancellationToken);

        if (!createIfMissing && !hasCurrentState)
        {
            return null;
        }

        var now = NormalizeTimestamp(timeProvider.GetUtcNow().UtcDateTime);
        session = new DemoSession
        {
            OwnerUserId = ownerUserId,
            SequenceNumber = await ResolveNextSequenceNumberAsync(ownerUserId, cancellationToken),
            SeedAsset = NormalizeAsset(optionsValue.DefaultSeedAsset),
            SeedAmount = ValidatePositiveAmount(optionsValue.DefaultSeedAmount),
            StartedAtUtc = now,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.Unknown
        };

        dbContext.DemoSessions.Add(session);

        if (hasCurrentState)
        {
            var bootstrapSummary = await BootstrapCurrentStateAsync(session, now, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    SystemActor,
                    "DemoSession.Bootstrapped",
                    $"DemoSession/{session.Id}",
                    bootstrapSummary,
                    CorrelationId: null,
                    Outcome: "Applied",
                    Environment: nameof(ExecutionEnvironment.Demo)),
                cancellationToken);
        }
        else
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    SystemActor,
                    "DemoSession.Started",
                    $"DemoSession/{session.Id}",
                    BuildStartContext(session.SequenceNumber, session.SeedAsset, session.SeedAmount),
                    CorrelationId: null,
                    Outcome: "Applied",
                    Environment: nameof(ExecutionEnvironment.Demo)),
                cancellationToken);
        }

        return session;
    }

    private async Task<bool> HasCurrentDemoStateAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        return await dbContext.DemoWallets.IgnoreQueryFilters().AnyAsync(
                   entity => entity.OwnerUserId == ownerUserId &&
                             !entity.IsDeleted &&
                             (entity.AvailableBalance != 0m || entity.ReservedBalance != 0m),
                   cancellationToken) ||
               await dbContext.DemoPositions.IgnoreQueryFilters().AnyAsync(
                   entity => entity.OwnerUserId == ownerUserId &&
                             !entity.IsDeleted &&
                             (entity.Quantity != 0m ||
                              entity.CostBasis != 0m ||
                              entity.RealizedPnl != 0m ||
                              entity.UnrealizedPnl != 0m ||
                              entity.TotalFeesInQuote != 0m ||
                              entity.NetFundingInQuote != 0m),
                   cancellationToken);
    }

    private async Task EnsureSeededPortfolioStateAsync(DemoSession session, CancellationToken cancellationToken)
    {
        var hasCurrentState = await HasCurrentDemoStateAsync(session.OwnerUserId, cancellationToken);
        if (hasCurrentState)
        {
            return;
        }

        var sessionStartedAtUtc = NormalizeTimestamp(session.StartedAtUtc);
        var hasLedgerActivity = await dbContext.DemoLedgerTransactions
            .IgnoreQueryFilters()
            .AnyAsync(
                entity => entity.OwnerUserId == session.OwnerUserId &&
                          !entity.IsDeleted &&
                          entity.CreatedDate >= sessionStartedAtUtc,
                cancellationToken);

        if (hasLedgerActivity)
        {
            return;
        }

        var now = NormalizeTimestamp(timeProvider.GetUtcNow().UtcDateTime);
        await SeedResetWalletAsync(session, session.SeedAsset, session.SeedAmount, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> ResolveNextSequenceNumberAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        var maxSequenceNumber = await dbContext.DemoSessions
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == ownerUserId && !entity.IsDeleted)
            .Select(entity => (int?)entity.SequenceNumber)
            .MaxAsync(cancellationToken);

        return (maxSequenceNumber ?? 0) + 1;
    }

    private async Task<string> BootstrapCurrentStateAsync(DemoSession session, DateTime occurredAtUtc, CancellationToken cancellationToken)
    {
        var walletCount = 0;
        var positionCount = 0;
        var wallets = await dbContext.DemoWallets
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted &&
                (entity.AvailableBalance != 0m || entity.ReservedBalance != 0m))
            .OrderBy(entity => entity.Asset)
            .ToListAsync(cancellationToken);
        var positions = await dbContext.DemoPositions
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == session.OwnerUserId &&
                !entity.IsDeleted &&
                (entity.Quantity != 0m ||
                 entity.CostBasis != 0m ||
                 entity.RealizedPnl != 0m ||
                 entity.UnrealizedPnl != 0m ||
                 entity.TotalFeesInQuote != 0m ||
                 entity.NetFundingInQuote != 0m ||
                 entity.LastMarkPrice.HasValue ||
                 entity.LastPrice.HasValue ||
                 entity.LastFillPrice.HasValue))
            .OrderBy(entity => entity.PositionScopeKey)
            .ThenBy(entity => entity.Symbol)
            .ToListAsync(cancellationToken);

        foreach (var wallet in wallets)
        {
            walletCount++;
            var transactionId = Guid.NewGuid();
            var transaction = new DemoLedgerTransaction
            {
                Id = transactionId,
                OwnerUserId = session.OwnerUserId,
                OperationId = $"demo-bootstrap:{session.SequenceNumber}:wallet:{walletCount}",
                TransactionType = DemoLedgerTransactionType.SessionBootstrapped,
                PositionScopeKey = PortfolioScopeKey,
                OccurredAtUtc = occurredAtUtc
            };

            dbContext.DemoLedgerTransactions.Add(transaction);
            dbContext.DemoLedgerEntries.Add(new DemoLedgerEntry
            {
                Id = Guid.NewGuid(),
                OwnerUserId = session.OwnerUserId,
                DemoLedgerTransactionId = transactionId,
                Asset = wallet.Asset,
                AvailableDelta = wallet.AvailableBalance,
                ReservedDelta = wallet.ReservedBalance,
                AvailableBalanceAfter = wallet.AvailableBalance,
                ReservedBalanceAfter = wallet.ReservedBalance
            });
        }

        await demoWalletValuationService.SyncAsync(wallets, cancellationToken);

        foreach (var position in positions)
        {
            positionCount++;
            dbContext.DemoLedgerTransactions.Add(new DemoLedgerTransaction
            {
                Id = Guid.NewGuid(),
                OwnerUserId = session.OwnerUserId,
                OperationId = $"demo-bootstrap:{session.SequenceNumber}:position:{positionCount}",
                TransactionType = DemoLedgerTransactionType.SessionBootstrapped,
                BotId = position.BotId,
                PositionScopeKey = position.PositionScopeKey,
                Symbol = position.Symbol,
                BaseAsset = position.BaseAsset,
                QuoteAsset = position.QuoteAsset,
                PositionKind = position.PositionKind,
                MarginMode = position.MarginMode,
                Leverage = position.Leverage,
                PositionQuantityAfter = position.Quantity,
                PositionCostBasisAfter = position.CostBasis,
                PositionAverageEntryPriceAfter = position.AverageEntryPrice,
                CumulativeRealizedPnlAfter = position.RealizedPnl,
                UnrealizedPnlAfter = position.UnrealizedPnl,
                CumulativeFeesInQuoteAfter = position.TotalFeesInQuote,
                NetFundingInQuoteAfter = position.NetFundingInQuote,
                LastPriceAfter = position.LastPrice,
                MarkPriceAfter = position.LastMarkPrice,
                MaintenanceMarginRateAfter = position.MaintenanceMarginRate,
                MaintenanceMarginAfter = position.MaintenanceMargin,
                MarginBalanceAfter = position.MarginBalance,
                LiquidationPriceAfter = position.LiquidationPrice,
                OccurredAtUtc = occurredAtUtc
            });
        }

        return $"Wallets={walletCount}; Positions={positionCount}; Baseline=CurrentState";
    }

    private async Task<int> TerminalizeOpenOrdersAsync(
        string ownerUserId,
        DateTime sessionStartedAtUtc,
        string? resetReason,
        ISet<Guid> affectedBotIds,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var orders = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted &&
                entity.ExecutionEnvironment == ExecutionEnvironment.Demo &&
                entity.CreatedDate >= sessionStartedAtUtc &&
                OpenOrderStates.Contains(entity.State))
            .ToListAsync(cancellationToken);

        foreach (var order in orders)
        {
            if (order.BotId.HasValue)
            {
                affectedBotIds.Add(order.BotId.Value);
            }

            var lastTransition = await dbContext.ExecutionOrderTransitions
                .IgnoreQueryFilters()
                .Where(entity => entity.ExecutionOrderId == order.Id && !entity.IsDeleted)
                .OrderByDescending(entity => entity.SequenceNumber)
                .FirstOrDefaultAsync(cancellationToken);
            var targetState = order.State is ExecutionOrderState.Submitted or ExecutionOrderState.PartiallyFilled
                ? ExecutionOrderState.Cancelled
                : ExecutionOrderState.Failed;
            var detail = BuildOrderResetDetail(resetReason);

            dbContext.ExecutionOrderTransitions.Add(
                ExecutionOrderStateMachine.Transition(
                    order,
                    sequenceNumber: (lastTransition?.SequenceNumber ?? 0) + 1,
                    targetState,
                    targetState == ExecutionOrderState.Cancelled ? "DemoSessionResetCancelled" : "DemoSessionResetFailedClosed",
                    occurredAtUtc,
                    CreateCorrelationId(),
                    lastTransition?.CorrelationId ?? order.RootCorrelationId,
                    detail));

            order.FailureCode = "DemoSessionReset";
            order.FailureDetail = Truncate(detail, 512);
        }

        return orders.Count;
    }

    private async Task ResetWalletsAsync(string ownerUserId, DateTime observedAtUtc, CancellationToken cancellationToken)
    {
        var wallets = await dbContext.DemoWallets
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == ownerUserId && !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var wallet in wallets)
        {
            wallet.AvailableBalance = 0m;
            wallet.ReservedBalance = 0m;
            wallet.LastActivityAtUtc = observedAtUtc;
        }

        await demoWalletValuationService.SyncAsync(wallets, cancellationToken);
    }

    private async Task ResetPositionsAsync(string ownerUserId, DateTime observedAtUtc, ISet<Guid> affectedBotIds, CancellationToken cancellationToken)
    {
        var positions = await dbContext.DemoPositions
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == ownerUserId && !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var position in positions)
        {
            if (position.BotId.HasValue)
            {
                affectedBotIds.Add(position.BotId.Value);
            }

            position.PositionKind = DemoPositionKind.Spot;
            position.MarginMode = null;
            position.Leverage = null;
            position.Quantity = 0m;
            position.CostBasis = 0m;
            position.AverageEntryPrice = 0m;
            position.RealizedPnl = 0m;
            position.UnrealizedPnl = 0m;
            position.TotalFeesInQuote = 0m;
            position.NetFundingInQuote = 0m;
            position.IsolatedMargin = null;
            position.MaintenanceMarginRate = null;
            position.MaintenanceMargin = null;
            position.MarginBalance = null;
            position.LiquidationPrice = null;
            position.LastMarkPrice = null;
            position.LastPrice = null;
            position.LastFillPrice = null;
            position.LastFundingRate = null;
            position.LastFilledAtUtc = null;
            position.LastValuationAtUtc = observedAtUtc;
            position.LastFundingAppliedAtUtc = null;
        }
    }

    private async Task SeedResetWalletAsync(DemoSession session, string asset, decimal amount, DateTime occurredAtUtc, CancellationToken cancellationToken)
    {
        var wallet = await dbContext.DemoWallets
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.OwnerUserId == session.OwnerUserId &&
                          entity.Asset == asset &&
                          !entity.IsDeleted,
                cancellationToken);

        if (wallet is null)
        {
            wallet = new DemoWallet { OwnerUserId = session.OwnerUserId, Asset = asset };
            dbContext.DemoWallets.Add(wallet);
        }

        wallet.AvailableBalance = amount;
        wallet.ReservedBalance = 0m;
        wallet.LastActivityAtUtc = occurredAtUtc;
        await demoWalletValuationService.SyncAsync(wallet, cancellationToken);

        var transactionId = Guid.NewGuid();
        var transaction = new DemoLedgerTransaction
        {
            Id = transactionId,
            OwnerUserId = session.OwnerUserId,
            OperationId = $"demo-session-reset:{session.SequenceNumber}:seed",
            TransactionType = DemoLedgerTransactionType.WalletSeeded,
            PositionScopeKey = PortfolioScopeKey,
            OccurredAtUtc = occurredAtUtc
        };

        dbContext.DemoLedgerTransactions.Add(transaction);
        dbContext.DemoLedgerEntries.Add(new DemoLedgerEntry
        {
            Id = Guid.NewGuid(),
            OwnerUserId = session.OwnerUserId,
            DemoLedgerTransactionId = transactionId,
            Asset = asset,
            AvailableDelta = amount,
            ReservedDelta = 0m,
            AvailableBalanceAfter = amount,
            ReservedBalanceAfter = 0m
        });
    }

    private async Task UpdateBotStateAsync(Guid botId, CancellationToken cancellationToken, int? openPositionCountOverride = null)
    {
        var bot = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(entity => entity.Id == botId && !entity.IsDeleted, cancellationToken);

        if (bot is null)
        {
            return;
        }

        bot.OpenOrderCount = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .CountAsync(entity => entity.BotId == botId && !entity.IsDeleted && OpenOrderStates.Contains(entity.State), cancellationToken);
        bot.OpenPositionCount = openPositionCountOverride ?? await dbContext.DemoPositions
            .IgnoreQueryFilters()
            .CountAsync(entity => entity.BotId == botId && !entity.IsDeleted && entity.Quantity != 0m, cancellationToken);
    }

    private static DemoSessionSnapshot MapSnapshot(DemoSession session)
    {
        return new DemoSessionSnapshot(
            session.Id,
            session.SequenceNumber,
            session.SeedAsset,
            session.SeedAmount,
            session.State,
            session.ConsistencyStatus,
            session.StartedAtUtc,
            session.ClosedAtUtc,
            session.LastConsistencyCheckedAtUtc,
            session.LastDriftDetectedAtUtc,
            session.LastDriftSummary);
    }

    private static void ApplyConsistency(DemoSession session, DemoConsistencyCheckResult result)
    {
        session.LastConsistencyCheckedAtUtc = result.EvaluatedAtUtc;
        session.ConsistencyStatus = result.Status;
        session.LastDriftDetectedAtUtc = result.Status == DemoConsistencyStatus.DriftDetected ? result.EvaluatedAtUtc : null;
        session.LastDriftSummary = result.Status == DemoConsistencyStatus.DriftDetected ? Truncate(result.Summary, 512) : null;
    }

    private static void EnsureDemoEnvironment(ExecutionEnvironment environment)
    {
        if (environment != ExecutionEnvironment.Demo)
        {
            throw new InvalidOperationException("Demo session controls are only available for the Demo execution environment.");
        }
    }

    private static string BuildStartContext(int sequenceNumber, string asset, decimal amount) => FormattableString.Invariant($"Sequence={sequenceNumber}; Seed={asset}:{FormatDecimal(amount)}");

    private static string BuildResetContext(int sequenceNumber, string asset, decimal amount, Guid? closedSessionId, int terminalizedOrderCount, string? resetReason, string? consistencySummary)
    {
        var parts = new List<string>
        {
            $"Sequence={sequenceNumber}",
            $"Seed={asset}:{FormatDecimal(amount)}",
            $"ClosedSession={(closedSessionId.HasValue ? closedSessionId.Value.ToString("N") : "none")}",
            $"OpenOrdersTerminalized={terminalizedOrderCount}"
        };

        if (!string.IsNullOrWhiteSpace(resetReason))
        {
            parts.Add($"Reason={resetReason}");
        }

        if (!string.IsNullOrWhiteSpace(consistencySummary))
        {
            parts.Add($"Consistency={Truncate(consistencySummary, 160)}");
        }

        return Truncate(string.Join(" | ", parts), 2048) ?? "Demo session reset applied.";
    }

    private static string BuildOrderResetDetail(string? resetReason) => string.IsNullOrWhiteSpace(resetReason) ? "Demo session reset applied." : $"Demo session reset applied. Reason={resetReason}";

    private static string BuildFailedOrderLeakReconciliationContext(int reconciledPositionCount, Guid? sampleExecutionOrderId)
    {
        var parts = new List<string>
        {
            $"ReconciledPositions={reconciledPositionCount}",
            "Reason=FailedDemoVirtualOrderLeak",
            $"SampleExecutionOrder={(sampleExecutionOrderId.HasValue ? sampleExecutionOrderId.Value.ToString("N") : "none")}"
        };

        return Truncate(string.Join(" | ", parts), 2048) ?? "Failed demo virtual order leak reconciled.";
    }

    private static string BuildFailedOrderWalletLeakReconciliationContext(int reconciledOrderCount, Guid? sampleExecutionOrderId)
    {
        var parts = new List<string>
        {
            $"ReconciledOrders={reconciledOrderCount}",
            "Reason=FailedDemoVirtualOrderWalletLeak",
            $"SampleExecutionOrder={(sampleExecutionOrderId.HasValue ? sampleExecutionOrderId.Value.ToString("N") : "none")}"
        };

        return Truncate(string.Join(" | ", parts), 2048) ?? "Failed demo virtual order wallet leak reconciled.";
    }

    private static string BuildMissingPositionProjectionRecoveryContext(int rehydratedPositionCount, string? samplePositionKey)
    {
        var parts = new List<string>
        {
            $"RehydratedPositions={rehydratedPositionCount}",
            "Reason=MissingDemoPositionProjection",
            $"SamplePositionKey={samplePositionKey ?? "none"}"
        };

        return Truncate(string.Join(" | ", parts), 2048) ?? "Missing demo position projection rehydrated.";
    }

    private static bool IsRecoverablePositionSnapshot(DemoLedgerTransaction transaction)
    {
        return !string.IsNullOrWhiteSpace(transaction.PositionScopeKey) &&
               !string.IsNullOrWhiteSpace(transaction.Symbol) &&
               !string.IsNullOrWhiteSpace(transaction.BaseAsset) &&
               !string.IsNullOrWhiteSpace(transaction.QuoteAsset) &&
               transaction.PositionQuantityAfter.HasValue &&
               transaction.PositionCostBasisAfter.HasValue &&
               transaction.PositionAverageEntryPriceAfter.HasValue &&
               transaction.CumulativeRealizedPnlAfter.HasValue &&
               transaction.UnrealizedPnlAfter.HasValue &&
               transaction.CumulativeFeesInQuoteAfter.HasValue;
    }

    private static DemoPosition CreatePositionFromLedgerSnapshot(string ownerUserId, DemoLedgerTransaction transaction)
    {
        var position = new DemoPosition
        {
            OwnerUserId = ownerUserId
        };

        ApplyPositionSnapshot(position, transaction);
        return position;
    }

    private static string CreatePositionKey(string positionScopeKey, string symbol) => $"{positionScopeKey}|{symbol}";

    private static void ApplyPositionSnapshot(DemoPosition position, DemoLedgerTransaction transaction)
    {
        position.BotId = transaction.BotId;
        position.PositionScopeKey = transaction.PositionScopeKey;
        position.Symbol = transaction.Symbol!;
        position.BaseAsset = transaction.BaseAsset!;
        position.QuoteAsset = transaction.QuoteAsset!;
        position.PositionKind = transaction.PositionKind ?? DemoPositionKind.Spot;
        position.MarginMode = transaction.MarginMode;
        position.Leverage = transaction.Leverage;
        position.Quantity = transaction.PositionQuantityAfter!.Value;
        position.CostBasis = transaction.PositionCostBasisAfter!.Value;
        position.AverageEntryPrice = transaction.PositionAverageEntryPriceAfter!.Value;
        position.RealizedPnl = transaction.CumulativeRealizedPnlAfter!.Value;
        position.UnrealizedPnl = transaction.UnrealizedPnlAfter!.Value;
        position.TotalFeesInQuote = transaction.CumulativeFeesInQuoteAfter!.Value;
        position.NetFundingInQuote = transaction.NetFundingInQuoteAfter ?? 0m;
        position.MaintenanceMarginRate = transaction.MaintenanceMarginRateAfter;
        position.MaintenanceMargin = transaction.MaintenanceMarginAfter;
        position.MarginBalance = transaction.MarginBalanceAfter;
        position.LiquidationPrice = transaction.LiquidationPriceAfter;
        position.LastMarkPrice = transaction.MarkPriceAfter;
        position.LastPrice = transaction.LastPriceAfter;
        position.LastFillPrice = transaction.TransactionType == DemoLedgerTransactionType.FillApplied ? transaction.Price : null;
        position.LastFundingRate = transaction.FundingRate;
        position.LastFilledAtUtc = transaction.TransactionType == DemoLedgerTransactionType.FillApplied
            ? NormalizeTimestamp(transaction.OccurredAtUtc)
            : null;
        position.LastValuationAtUtc = transaction.MarkPriceAfter.HasValue
            ? NormalizeTimestamp(transaction.OccurredAtUtc)
            : null;
        position.LastFundingAppliedAtUtc = transaction.FundingRate.HasValue
            ? NormalizeTimestamp(transaction.OccurredAtUtc)
            : null;
    }

    private static string CreateCorrelationId() => Guid.NewGuid().ToString("N");

    private static string NormalizeRequired(string? value, string parameterName, int maxLength = 450)
    {
        var normalized = NormalizeOptional(value, maxLength);
        return !string.IsNullOrWhiteSpace(normalized) ? normalized : throw new ArgumentException("The value is required.", parameterName);
    }

    private static string NormalizeAsset(string? value)
    {
        var normalized = NormalizeRequired(value, nameof(value), 32).ToUpperInvariant();
        return normalized.Length <= 32 ? normalized : throw new ArgumentOutOfRangeException(nameof(value), "Asset codes cannot exceed 32 characters.");
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : throw new ArgumentOutOfRangeException(nameof(value), $"The value cannot exceed {maxLength} characters.");
    }

    private static decimal ValidatePositiveAmount(decimal amount) => amount > 0m ? amount : throw new ArgumentOutOfRangeException(nameof(amount), "The value must be greater than zero.");

    private static string FormatDecimal(decimal value) => value.ToString("0.##################", CultureInfo.InvariantCulture);

    private static string? Truncate(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= maxLength ? value : value[..maxLength];

    private decimal ClampToZero(decimal value) => Math.Abs(value) <= optionsValue.ConsistencyTolerance ? 0m : value;

    private static DateTime NormalizeTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private sealed record FailedOrderLeakReconciliationResult(
        int ReconciledPositionCount,
        Dictionary<Guid, int> OpenPositionCountsByBotId,
        Guid? SampleExecutionOrderId)
    {
        public static FailedOrderLeakReconciliationResult Empty => new(0, new Dictionary<Guid, int>(), null);
    }

    private sealed record FailedOrderWalletLeakReconciliationResult(
        int ReconciledOrderCount,
        Guid? SampleExecutionOrderId)
    {
        public static FailedOrderWalletLeakReconciliationResult Empty => new(0, null);
    }

    private sealed record MissingPositionProjectionRecoveryResult(
        int RehydratedPositionCount,
        HashSet<Guid> AffectedBotIds,
        string? SamplePositionKey)
    {
        public static MissingPositionProjectionRecoveryResult Empty => new(0, [], null);
    }

    private sealed record FailedOrderWalletLedgerDelta(
        string OrderId,
        string Asset,
        decimal AvailableDelta,
        decimal ReservedDelta);
}
