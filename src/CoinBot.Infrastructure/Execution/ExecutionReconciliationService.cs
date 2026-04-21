using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Execution;

public sealed class ExecutionReconciliationService(
    ApplicationDbContext dbContext,
    IExchangeCredentialService exchangeCredentialService,
    IBinancePrivateRestClient privateRestClient,
    IBinanceSpotPrivateRestClient spotPrivateRestClient,
    ExecutionOrderLifecycleService executionOrderLifecycleService,
    ILogger<ExecutionReconciliationService> logger,
    IOptions<ExecutionRuntimeOptions>? executionRuntimeOptions = null)
{
    private const string SystemActor = "system:execution-reconciliation";
    private static readonly ExecutionOrderState[] OpenStates =
    [
        ExecutionOrderState.Submitted,
        ExecutionOrderState.PartiallyFilled,
        ExecutionOrderState.CancelRequested
    ];
    private static readonly ExecutionOrderState[] TerminalStatesRequiringClosure =
    [
        ExecutionOrderState.Filled,
        ExecutionOrderState.Cancelled,
        ExecutionOrderState.Rejected,
        ExecutionOrderState.Failed
    ];
    private readonly ExecutionRuntimeOptions executionRuntimeOptionsValue = executionRuntimeOptions?.Value ?? new ExecutionRuntimeOptions();

    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var includeBrokerBackedDemoOrders = !executionRuntimeOptionsValue.AllowInternalDemoExecution;
        var orders = await dbContext.ExecutionOrders
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                (entity.ExecutionEnvironment == ExecutionEnvironment.Live ||
                 (includeBrokerBackedDemoOrders &&
                  entity.ExecutionEnvironment == ExecutionEnvironment.Demo)) &&
                entity.ExecutorKind == ExecutionOrderExecutorKind.Binance &&
                entity.ExchangeAccountId.HasValue &&
                (OpenStates.Contains(entity.State) ||
                 (entity.SubmittedToBroker &&
                  TerminalStatesRequiringClosure.Contains(entity.State) &&
                  !string.IsNullOrWhiteSpace(entity.ExternalOrderId) &&
                  (entity.LastReconciledAtUtc == null ||
                   entity.ReconciliationStatus == ExchangeStateDriftStatus.Unknown))))
            .OrderBy(entity => entity.LastReconciledAtUtc ?? DateTime.MinValue)
            .Select(entity => new ExecutionOrderDescriptor(
                entity.Id,
                entity.ExchangeAccountId!.Value,
                entity.Plane,
                entity.Symbol,
                entity.State,
                entity.Quantity,
                entity.FilledQuantity,
                entity.ExternalOrderId))
            .ToListAsync(cancellationToken);

        var reconciledCount = 0;

        foreach (var accountOrders in orders.GroupBy(order => new { order.ExchangeAccountId, order.Plane }))
        {
            ExchangeCredentialAccessResult credentialAccess;

            try
            {
                credentialAccess = await exchangeCredentialService.GetAsync(
                    new ExchangeCredentialAccessRequest(
                        accountOrders.Key.ExchangeAccountId,
                        SystemActor,
                        ExchangeCredentialAccessPurpose.Synchronization),
                    cancellationToken);
            }
            catch (InvalidOperationException)
            {
                foreach (var blockedOrder in accountOrders)
                {
                    await executionOrderLifecycleService.RecordReconciliationAsync(
                        blockedOrder.ExecutionOrderId,
                        ExchangeStateDriftStatus.Unknown,
                        "Credential access is blocked for execution reconciliation.",
                        cancellationToken);
                }

                logger.LogInformation(
                    "Execution reconciliation skipped for account {ExchangeAccountId} on plane {Plane} because synchronization access is blocked.",
                    accountOrders.Key.ExchangeAccountId,
                    accountOrders.Key.Plane);

                continue;
            }

            foreach (var order in accountOrders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var snapshot = await ResolveRestClient(order.Plane).GetOrderAsync(
                        new BinanceOrderQueryRequest(
                            order.ExchangeAccountId,
                            order.Symbol,
                            order.ExternalOrderId,
                            ExecutionClientOrderId.Create(order.ExecutionOrderId),
                            credentialAccess.ApiKey,
                            credentialAccess.ApiSecret),
                        cancellationToken);
                    var driftDetected = DetectDrift(order, snapshot);
                    var reconciliationStatus = driftDetected
                        ? ExchangeStateDriftStatus.DriftDetected
                        : ExchangeStateDriftStatus.InSync;

                    await executionOrderLifecycleService.ApplyReconciliationAsync(
                        order.ExecutionOrderId,
                        snapshot,
                        reconciliationStatus,
                        BuildSummary(order, snapshot, reconciliationStatus),
                        cancellationToken);

                    reconciledCount++;
                }
                catch (Exception)
                {
                    await executionOrderLifecycleService.RecordReconciliationAsync(
                        order.ExecutionOrderId,
                        ExchangeStateDriftStatus.Unknown,
                        "Execution reconciliation failed before exchange/system comparison completed.",
                        cancellationToken);

                    logger.LogWarning(
                        "Execution reconciliation failed for order {ExecutionOrderId}.",
                        order.ExecutionOrderId);
                }
            }
        }

        return reconciledCount;
    }

    private static bool DetectDrift(ExecutionOrderDescriptor order, BinanceOrderStatusSnapshot snapshot)
    {
        var exchangeState = MapExchangeStatus(snapshot.Status);
        var normalizedExchangeQuantity = Math.Min(order.Quantity, snapshot.ExecutedQuantity);

        if (!string.IsNullOrWhiteSpace(order.ExternalOrderId) &&
            !string.Equals(order.ExternalOrderId, snapshot.ExchangeOrderId, StringComparison.Ordinal))
        {
            return true;
        }

        return order.State != exchangeState ||
               order.FilledQuantity != normalizedExchangeQuantity;
    }

    private static string BuildSummary(
        ExecutionOrderDescriptor order,
        BinanceOrderStatusSnapshot snapshot,
        ExchangeStateDriftStatus reconciliationStatus)
    {
        return reconciliationStatus == ExchangeStateDriftStatus.DriftDetected
            ? $"LocalState={order.State}; ExchangeState={MapExchangeStatus(snapshot.Status)}; LocalFilledQuantity={FormatDecimal(order.FilledQuantity)}; ExchangeFilledQuantity={FormatDecimal(snapshot.ExecutedQuantity)}; Source={snapshot.Source}"
            : $"LocalState={order.State}; ExchangeState={MapExchangeStatus(snapshot.Status)}; FilledQuantity={FormatDecimal(snapshot.ExecutedQuantity)}; Source={snapshot.Source}";
    }

    private static ExecutionOrderState MapExchangeStatus(string status)
    {
        return status.Trim().ToUpperInvariant() switch
        {
            "NEW" => ExecutionOrderState.Submitted,
            "PARTIALLY_FILLED" => ExecutionOrderState.PartiallyFilled,
            "FILLED" => ExecutionOrderState.Filled,
            "CANCELED" => ExecutionOrderState.Cancelled,
            "EXPIRED" => ExecutionOrderState.Cancelled,
            "PENDING_CANCEL" => ExecutionOrderState.CancelRequested,
            "REJECTED" => ExecutionOrderState.Rejected,
            _ => ExecutionOrderState.Submitted
        };
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.##################", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record ExecutionOrderDescriptor(
        Guid ExecutionOrderId,
        Guid ExchangeAccountId,
        ExchangeDataPlane Plane,
        string Symbol,
        ExecutionOrderState State,
        decimal Quantity,
        decimal FilledQuantity,
        string? ExternalOrderId);

    private IBinanceOrderQueryClient ResolveRestClient(ExchangeDataPlane plane)
    {
        return plane == ExchangeDataPlane.Spot
            ? new SpotOrderQueryClientAdapter(spotPrivateRestClient)
            : new FuturesOrderQueryClientAdapter(privateRestClient);
    }

    private interface IBinanceOrderQueryClient
    {
        Task<BinanceOrderStatusSnapshot> GetOrderAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default);
    }

    private sealed class FuturesOrderQueryClientAdapter(IBinancePrivateRestClient inner) : IBinanceOrderQueryClient
    {
        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            return inner.GetOrderAsync(request, cancellationToken);
        }
    }

    private sealed class SpotOrderQueryClientAdapter(IBinanceSpotPrivateRestClient inner) : IBinanceOrderQueryClient
    {
        public Task<BinanceOrderStatusSnapshot> GetOrderAsync(
            BinanceOrderQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            return inner.GetOrderAsync(request, cancellationToken);
        }
    }
}
