using System.Globalization;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Execution;

public sealed class ExecutionOrderLifecycleService(
    ApplicationDbContext dbContext,
    IAuditLogService auditLogService,
    TimeProvider timeProvider,
    ILogger<ExecutionOrderLifecycleService> logger,
    IAlertDispatchCoordinator? alertDispatchCoordinator = null,
    IHostEnvironment? hostEnvironment = null,
    UserOperationsStreamHub? userOperationsStreamHub = null)
{
    private const string SystemActor = "system:execution-order-lifecycle";
    private static readonly ExecutionOrderState[] OpenStates =
    [
        ExecutionOrderState.Received,
        ExecutionOrderState.GatePassed,
        ExecutionOrderState.Dispatching,
        ExecutionOrderState.Submitted,
        ExecutionOrderState.PartiallyFilled
    ];

    public async Task<bool> ApplyExchangeUpdateAsync(
        BinanceOrderStatusSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var order = await ResolveOrderAsync(snapshot, cancellationToken);

        if (order is null)
        {
            logger.LogDebug(
                "Execution order update ignored because no local order matched the Binance client/exchange order ids.");
            return false;
        }

        await ApplySnapshotAsync(
            order,
            snapshot,
            reconciliationStatus: null,
            reconciliationSummary: null,
            cancellationToken);

        return true;
    }

    public async Task ApplyReconciliationAsync(
        Guid executionOrderId,
        BinanceOrderStatusSnapshot snapshot,
        ExchangeStateDriftStatus reconciliationStatus,
        string? reconciliationSummary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var order = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .SingleAsync(
                entity => entity.Id == executionOrderId &&
                          !entity.IsDeleted,
                cancellationToken);

        await ApplySnapshotAsync(
            order,
            snapshot,
            reconciliationStatus,
            reconciliationSummary,
            cancellationToken);
    }

    public async Task RecordReconciliationAsync(
        Guid executionOrderId,
        ExchangeStateDriftStatus reconciliationStatus,
        string? reconciliationSummary,
        CancellationToken cancellationToken = default)
    {
        var order = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .SingleAsync(
                entity => entity.Id == executionOrderId &&
                          !entity.IsDeleted,
                cancellationToken);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        ApplyReconciliationState(order, reconciliationStatus, reconciliationSummary, utcNow);
        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                SystemActor,
                "ExecutionOrder.Reconciliation",
                $"ExecutionOrder/{order.Id}",
                TrimToLength(
                    $"Status={reconciliationStatus}; Summary={TrimToLength(reconciliationSummary, 320) ?? "none"}",
                    2048),
                order.RootCorrelationId,
                MapReconciliationOutcome(reconciliationStatus),
                order.ExecutionEnvironment.ToString()),
            cancellationToken);

        await UpdateBotOpenOrderCountAsync(order.BotId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplySnapshotAsync(
        ExecutionOrder order,
        BinanceOrderStatusSnapshot snapshot,
        ExchangeStateDriftStatus? reconciliationStatus,
        string? reconciliationSummary,
        CancellationToken cancellationToken)
    {
        var normalizedSnapshot = NormalizeSnapshot(snapshot);
        var previousState = order.State;
        var previousFilledQuantity = order.FilledQuantity;
        var previousReconciliationStatus = order.ReconciliationStatus;
        var previousReconciliationSummary = order.ReconciliationSummary;
        var targetState = MapExchangeStatus(normalizedSnapshot.Status);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var stateChanged = false;
        var fillProgressAdvanced = false;
        ExecutionOrderTransition? transition = null;

        if (string.IsNullOrWhiteSpace(order.ExternalOrderId))
        {
            order.ExternalOrderId = normalizedSnapshot.ExchangeOrderId;
        }

        ApplyFillState(order, normalizedSnapshot);
        fillProgressAdvanced = order.FilledQuantity > previousFilledQuantity;

        if (reconciliationStatus.HasValue)
        {
            ApplyReconciliationState(order, reconciliationStatus.Value, reconciliationSummary, utcNow);
        }

        if (ShouldPersistTransition(previousState, targetState, fillProgressAdvanced))
        {
            var lastTransition = await GetLastTransitionAsync(order.Id, cancellationToken);
            transition = ExecutionOrderStateMachine.Transition(
                order,
                sequenceNumber: (lastTransition?.SequenceNumber ?? 0) + 1,
                targetState,
                BuildEventCode(previousState, targetState, fillProgressAdvanced),
                NormalizeTimestamp(normalizedSnapshot.EventTimeUtc),
                CreateCorrelationId(),
                lastTransition?.CorrelationId ?? order.RootCorrelationId,
                BuildTransitionDetail(normalizedSnapshot, reconciliationStatus, reconciliationSummary));

            dbContext.ExecutionOrderTransitions.Add(transition);
            stateChanged = previousState != targetState;
        }

        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                SystemActor,
                reconciliationStatus.HasValue
                    ? "ExecutionOrder.Reconciliation"
                    : "ExecutionOrder.ExchangeUpdate",
                $"ExecutionOrder/{order.Id}",
                BuildAuditContext(
                    normalizedSnapshot,
                    order,
                    previousState,
                    fillProgressAdvanced,
                    reconciliationStatus,
                    reconciliationSummary),
                order.RootCorrelationId,
                ResolveOutcome(
                    reconciliationStatus,
                    stateChanged,
                    fillProgressAdvanced,
                    previousReconciliationStatus,
                    previousReconciliationSummary,
                    order),
                order.ExecutionEnvironment.ToString()),
            cancellationToken);

        await UpdateBotOpenOrderCountAsync(order.BotId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (transition is not null)
        {
            await TrySendExecutionAlertAsync(order, transition, normalizedSnapshot, cancellationToken);
            userOperationsStreamHub?.Publish(
                new UserOperationsUpdate(
                    order.OwnerUserId,
                    "ExecutionChanged",
                    order.BotId,
                    order.Id,
                    transition.State.ToString(),
                    order.FailureCode ?? normalizedSnapshot.Status,
                    transition.OccurredAtUtc));
        }

        logger.LogInformation(
            "Execution order {ExecutionOrderId} synchronized to state {ExecutionOrderState}.",
            order.Id,
            order.State);
    }

    private async Task<ExecutionOrder?> ResolveOrderAsync(
        BinanceOrderStatusSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (ExecutionClientOrderId.TryParse(snapshot.ClientOrderId, out var executionOrderId))
        {
            return await dbContext.ExecutionOrders
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(
                    entity => entity.Id == executionOrderId &&
                              !entity.IsDeleted,
                    cancellationToken);
        }

        return await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.ExternalOrderId == snapshot.ExchangeOrderId &&
                          !entity.IsDeleted,
                cancellationToken);
    }

    private async Task<ExecutionOrderTransition?> GetLastTransitionAsync(
        Guid executionOrderId,
        CancellationToken cancellationToken)
    {
        return await dbContext.ExecutionOrderTransitions
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.ExecutionOrderId == executionOrderId &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.SequenceNumber)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task UpdateBotOpenOrderCountAsync(Guid? botId, CancellationToken cancellationToken)
    {
        if (!botId.HasValue)
        {
            return;
        }

        var bot = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.Id == botId.Value &&
                          !entity.IsDeleted,
                cancellationToken);

        if (bot is null)
        {
            return;
        }

        bot.OpenOrderCount = await dbContext.ExecutionOrders
            .IgnoreQueryFilters()
            .CountAsync(
                entity => entity.BotId == botId.Value &&
                          !entity.IsDeleted &&
                          OpenStates.Contains(entity.State),
                cancellationToken);
    }

    private static BinanceOrderStatusSnapshot NormalizeSnapshot(BinanceOrderStatusSnapshot snapshot)
    {
        return snapshot with
        {
            Symbol = NormalizeCode(snapshot.Symbol),
            ExchangeOrderId = NormalizeRequired(snapshot.ExchangeOrderId),
            ClientOrderId = NormalizeRequired(snapshot.ClientOrderId),
            Status = NormalizeCode(snapshot.Status),
            Source = NormalizeRequired(snapshot.Source),
            EventTimeUtc = NormalizeTimestamp(snapshot.EventTimeUtc)
        };
    }

    private static void ApplyFillState(ExecutionOrder order, BinanceOrderStatusSnapshot snapshot)
    {
        var normalizedExecutedQuantity = Math.Min(order.Quantity, Math.Max(order.FilledQuantity, snapshot.ExecutedQuantity));
        order.FilledQuantity = normalizedExecutedQuantity;

        if (snapshot.AveragePrice > 0m)
        {
            order.AverageFillPrice = snapshot.AveragePrice;
        }
        else if (snapshot.LastExecutedPrice > 0m && normalizedExecutedQuantity > 0m && !order.AverageFillPrice.HasValue)
        {
            order.AverageFillPrice = snapshot.LastExecutedPrice;
        }

        if (normalizedExecutedQuantity > 0m)
        {
            order.LastFilledAtUtc = NormalizeTimestamp(snapshot.EventTimeUtc);
        }
    }

    private static void ApplyReconciliationState(
        ExecutionOrder order,
        ExchangeStateDriftStatus reconciliationStatus,
        string? reconciliationSummary,
        DateTime observedAtUtc)
    {
        order.LastReconciledAtUtc = NormalizeTimestamp(observedAtUtc);
        order.ReconciliationStatus = reconciliationStatus;
        order.ReconciliationSummary = TrimToLength(reconciliationSummary, 512);
        order.LastDriftDetectedAtUtc = reconciliationStatus == ExchangeStateDriftStatus.DriftDetected
            ? NormalizeTimestamp(observedAtUtc)
            : null;
    }

    private static ExecutionOrderState MapExchangeStatus(string status)
    {
        return status switch
        {
            "NEW" => ExecutionOrderState.Submitted,
            "PARTIALLY_FILLED" => ExecutionOrderState.PartiallyFilled,
            "FILLED" => ExecutionOrderState.Filled,
            "CANCELED" => ExecutionOrderState.Cancelled,
            "EXPIRED" => ExecutionOrderState.Cancelled,
            "PENDING_CANCEL" => ExecutionOrderState.Cancelled,
            "REJECTED" => ExecutionOrderState.Rejected,
            _ => ExecutionOrderState.Submitted
        };
    }

    private static bool ShouldPersistTransition(
        ExecutionOrderState currentState,
        ExecutionOrderState targetState,
        bool fillProgressAdvanced)
    {
        if (targetState == currentState)
        {
            return targetState == ExecutionOrderState.PartiallyFilled && fillProgressAdvanced;
        }

        if (IsTerminalState(currentState))
        {
            return false;
        }

        return GetStateRank(targetState) >= GetStateRank(currentState);
    }

    private static bool IsTerminalState(ExecutionOrderState state)
    {
        return state is ExecutionOrderState.Filled or
            ExecutionOrderState.Cancelled or
            ExecutionOrderState.Rejected or
            ExecutionOrderState.Failed;
    }

    private static int GetStateRank(ExecutionOrderState state)
    {
        return state switch
        {
            ExecutionOrderState.Received => 0,
            ExecutionOrderState.GatePassed => 1,
            ExecutionOrderState.Dispatching => 2,
            ExecutionOrderState.Submitted => 3,
            ExecutionOrderState.PartiallyFilled => 4,
            ExecutionOrderState.Filled => 5,
            ExecutionOrderState.Cancelled => 5,
            ExecutionOrderState.Rejected => 5,
            ExecutionOrderState.Failed => 5,
            _ => 0
        };
    }

    private static string BuildEventCode(
        ExecutionOrderState previousState,
        ExecutionOrderState targetState,
        bool fillProgressAdvanced)
    {
        if (targetState == ExecutionOrderState.PartiallyFilled &&
            previousState == ExecutionOrderState.PartiallyFilled &&
            fillProgressAdvanced)
        {
            return "ExchangePartialFillProgressed";
        }

        return targetState switch
        {
            ExecutionOrderState.Submitted => "ExchangeSubmitted",
            ExecutionOrderState.PartiallyFilled => "ExchangePartiallyFilled",
            ExecutionOrderState.Filled => "ExchangeFilled",
            ExecutionOrderState.Cancelled => "ExchangeCancelled",
            ExecutionOrderState.Rejected => "ExchangeRejected",
            _ => "ExchangeObserved"
        };
    }

    private static string BuildTransitionDetail(
        BinanceOrderStatusSnapshot snapshot,
        ExchangeStateDriftStatus? reconciliationStatus,
        string? reconciliationSummary)
    {
        var parts = new List<string>
        {
            $"Source={snapshot.Source}",
            $"ExchangeStatus={snapshot.Status}",
            $"ExecutedQuantity={FormatDecimal(snapshot.ExecutedQuantity)}",
            $"AveragePrice={FormatDecimal(snapshot.AveragePrice)}"
        };

        if (reconciliationStatus.HasValue)
        {
            parts.Add($"ReconciliationStatus={reconciliationStatus.Value}");
        }

        if (!string.IsNullOrWhiteSpace(reconciliationSummary))
        {
            parts.Add($"ReconciliationSummary={TrimToLength(reconciliationSummary, 160)}");
        }

        return TrimToLength(string.Join("; ", parts), 512) ?? "Exchange update applied.";
    }

    private static string BuildAuditContext(
        BinanceOrderStatusSnapshot snapshot,
        ExecutionOrder order,
        ExecutionOrderState previousState,
        bool fillProgressAdvanced,
        ExchangeStateDriftStatus? reconciliationStatus,
        string? reconciliationSummary)
    {
        var parts = new List<string>
        {
            $"Source={snapshot.Source}",
            $"ExchangeStatus={snapshot.Status}",
            $"PreviousState={previousState}",
            $"CurrentState={order.State}",
            $"FilledQuantity={FormatDecimal(order.FilledQuantity)}",
            $"FillProgressAdvanced={fillProgressAdvanced}"
        };

        if (order.StopLossPrice.HasValue || order.TakeProfitPrice.HasValue)
        {
            parts.Add(
                $"ProtectiveTargets=SL:{FormatNullableDecimal(order.StopLossPrice)}|TP:{FormatNullableDecimal(order.TakeProfitPrice)}");
        }

        if (reconciliationStatus.HasValue)
        {
            parts.Add($"ReconciliationStatus={reconciliationStatus.Value}");
        }

        if (!string.IsNullOrWhiteSpace(reconciliationSummary))
        {
            parts.Add($"ReconciliationSummary={TrimToLength(reconciliationSummary, 256)}");
        }

        return TrimToLength(string.Join(" | ", parts), 2048) ?? "Execution order exchange update.";
    }

    private static string ResolveOutcome(
        ExchangeStateDriftStatus? reconciliationStatus,
        bool stateChanged,
        bool fillProgressAdvanced,
        ExchangeStateDriftStatus previousReconciliationStatus,
        string? previousReconciliationSummary,
        ExecutionOrder order)
    {
        if (reconciliationStatus.HasValue)
        {
            if (reconciliationStatus.Value == ExchangeStateDriftStatus.DriftDetected)
            {
                return "Reconciled:DriftDetected";
            }

            if (reconciliationStatus.Value == ExchangeStateDriftStatus.InSync)
            {
                return previousReconciliationStatus != ExchangeStateDriftStatus.InSync ||
                       !string.Equals(previousReconciliationSummary, order.ReconciliationSummary, StringComparison.Ordinal)
                    ? "Reconciled:InSync"
                    : "Reconciled:Observed";
            }

            return "Reconciled:Unknown";
        }

        if (stateChanged)
        {
            return "Applied:StateChanged";
        }

        if (fillProgressAdvanced)
        {
            return "Applied:FillProgressed";
        }

        return "Observed";
    }

    private static string MapReconciliationOutcome(ExchangeStateDriftStatus reconciliationStatus)
    {
        return reconciliationStatus switch
        {
            ExchangeStateDriftStatus.InSync => "Reconciled:InSync",
            ExchangeStateDriftStatus.DriftDetected => "Reconciled:DriftDetected",
            _ => "Reconciled:Unknown"
        };
    }

    private static string CreateCorrelationId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string NormalizeCode(string? value)
    {
        return NormalizeRequired(value).ToUpperInvariant();
    }

    private static string NormalizeRequired(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidOperationException("A required exchange value was missing.");
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

    private static string? TrimToLength(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalizedValue = value.Trim();

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.##################", CultureInfo.InvariantCulture);
    }

    private static string FormatNullableDecimal(decimal? value)
    {
        return value.HasValue
            ? FormatDecimal(value.Value)
            : "none";
    }

    private async Task TrySendExecutionAlertAsync(
        ExecutionOrder order,
        ExecutionOrderTransition transition,
        BinanceOrderStatusSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (alertDispatchCoordinator is null)
        {
            return;
        }

        var eventType = transition.State switch
        {
            ExecutionOrderState.Filled => "OrderFilled",
            ExecutionOrderState.Rejected => "OrderRejected",
            ExecutionOrderState.Cancelled => "OrderCancelled",
            _ => null
        };

        if (eventType is null)
        {
            return;
        }

        var botLabel = order.BotId.HasValue
            ? order.BotId.Value.ToString("N")[..12]
            : "none";

        await alertDispatchCoordinator.SendAsync(
            new CoinBot.Application.Abstractions.Alerts.AlertNotification(
                Code: $"ORDER_{transition.State.ToString().ToUpperInvariant()}",
                Severity: transition.State == ExecutionOrderState.Filled
                    ? CoinBot.Application.Abstractions.Alerts.AlertSeverity.Information
                    : CoinBot.Application.Abstractions.Alerts.AlertSeverity.Warning,
                Title: eventType,
                Message:
                    $"EventType={eventType}; BotId={botLabel}; Symbol={order.Symbol}; Result={transition.State}; FailureCode={order.FailureCode ?? snapshot.Status}; ClientOrderId={snapshot.ClientOrderId}; TimestampUtc={transition.OccurredAtUtc:O}; Environment={ResolveExecutionEnvironmentLabel(order.ExecutionEnvironment)}",
                CorrelationId: transition.CorrelationId),
            $"order-transition:{transition.Id:N}",
            TimeSpan.FromDays(7),
            cancellationToken);
    }

    private string ResolveExecutionEnvironmentLabel(ExecutionEnvironment executionEnvironment)
    {
        var runtimeLabel = hostEnvironment?.EnvironmentName ?? "Unknown";
        var executionLabel = hostEnvironment?.IsDevelopment() == true && executionEnvironment == ExecutionEnvironment.Live
            ? "Testnet"
            : executionEnvironment.ToString();

        return $"{runtimeLabel}/{executionLabel}";
    }
}
