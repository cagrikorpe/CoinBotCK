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
    private static readonly IReadOnlySet<ExecutionOrderState> OpenOrderStates = new HashSet<ExecutionOrderState>
    {
        ExecutionOrderState.Received,
        ExecutionOrderState.GatePassed,
        ExecutionOrderState.Dispatching,
        ExecutionOrderState.Submitted,
        ExecutionOrderState.PartiallyFilled
    };

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

    private async Task UpdateBotStateAsync(Guid botId, CancellationToken cancellationToken)
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
        bot.OpenPositionCount = await dbContext.DemoPositions
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
