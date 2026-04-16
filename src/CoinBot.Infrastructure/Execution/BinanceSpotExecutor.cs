using System.Globalization;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Execution;

public sealed class BinanceSpotExecutor(
    ApplicationDbContext dbContext,
    IExchangeCredentialService exchangeCredentialService,
    IBinanceSpotPrivateRestClient privateRestClient,
    ILogger<BinanceSpotExecutor> logger,
    IDependencyCircuitBreakerStateManager? dependencyCircuitBreakerStateManager = null,
    IMarketDataService? marketDataService = null,
    IBinanceExchangeInfoClient? exchangeInfoClient = null) : IExecutionTargetExecutor
{
    private const string BreakerActor = "system:order-execution";

    public ExecutionOrderExecutorKind Kind => ExecutionOrderExecutorKind.Binance;

    public async Task<ExecutionTargetDispatchResult> DispatchAsync(
        ExecutionOrder order,
        ExecutionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(command);

        var exchangeAccountId = command.ExchangeAccountId
            ?? throw new InvalidOperationException("Live execution requires an exchange account.");
        var exchangeAccount = await dbContext.ExchangeAccounts
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.Id == exchangeAccountId &&
                          !entity.IsDeleted,
                cancellationToken)
            ?? throw new InvalidOperationException($"Exchange account '{exchangeAccountId}' was not found.");

        if (!string.Equals(exchangeAccount.OwnerUserId, command.OwnerUserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Exchange account owner does not match the execution command owner.");
        }

        if (!string.Equals(exchangeAccount.ExchangeName, "Binance", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only Binance exchange accounts are supported by the live execution core.");
        }

        if (exchangeAccount.IsReadOnly)
        {
            throw new InvalidOperationException("Live execution is blocked because the exchange account is read-only.");
        }

        try
        {
            await EnsureSpotTradingCapabilityAsync(exchangeAccountId, command.OwnerUserId, cancellationToken);

            var credentialAccess = await exchangeCredentialService.GetAsync(
                new ExchangeCredentialAccessRequest(
                    exchangeAccountId,
                    command.Actor,
                    ExchangeCredentialAccessPurpose.Execution,
                    order.RootCorrelationId),
                cancellationToken);
            var symbolMetadata = await ResolveSymbolMetadataAsync(command.Symbol, cancellationToken);
            ValidateOrderPreflight(command, symbolMetadata);
            await ValidateBalanceAvailabilityAsync(exchangeAccountId, command, symbolMetadata, cancellationToken);

            var placementResult = await privateRestClient.PlaceOrderAsync(
                new BinanceOrderPlacementRequest(
                    exchangeAccountId,
                    command.Symbol,
                    command.Side,
                    command.OrderType,
                    command.Quantity,
                    command.Price,
                    ExecutionClientOrderId.Create(order.Id),
                    credentialAccess.ApiKey,
                    credentialAccess.ApiSecret,
                    order.IdempotencyKey,
                    order.RootCorrelationId,
                    ExecutionAttemptId: null,
                    order.Id,
                    command.OwnerUserId,
                    ReduceOnly: false,
                    QuoteOrderQuantity: null,
                    TimeInForce: ResolveTimeInForce(command)),
                cancellationToken);

            if (dependencyCircuitBreakerStateManager is not null)
            {
                await dependencyCircuitBreakerStateManager.RecordSuccessAsync(
                    new DependencyCircuitBreakerSuccessRequest(
                        DependencyCircuitBreakerKind.OrderExecution,
                        BreakerActor,
                        order.RootCorrelationId),
                    cancellationToken);
            }

            logger.LogInformation(
                "Binance spot executor submitted order {ExecutionOrderId} for {Symbol}.",
                order.Id,
                command.Symbol);

            return new ExecutionTargetDispatchResult(
                placementResult.OrderId,
                placementResult.SubmittedAtUtc,
                BuildDispatchDetail(placementResult),
                placementResult.Snapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (dependencyCircuitBreakerStateManager is not null &&
                ShouldRecordBreakerFailure(exception))
            {
                await dependencyCircuitBreakerStateManager.RecordFailureAsync(
                    new DependencyCircuitBreakerFailureRequest(
                        DependencyCircuitBreakerKind.OrderExecution,
                        BreakerActor,
                        ResolveFailureCode(exception),
                        Truncate(exception.Message, 512) ?? "Order execution failed.",
                        order.RootCorrelationId),
                    cancellationToken);
            }

            throw;
        }
    }

    private async Task EnsureSpotTradingCapabilityAsync(
        Guid exchangeAccountId,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        var latestValidation = await dbContext.ApiCredentialValidations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.ExchangeAccountId == exchangeAccountId &&
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.ValidatedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestValidation is null ||
            !latestValidation.IsKeyValid ||
            !latestValidation.CanTrade ||
            !latestValidation.SupportsSpot)
        {
            throw new ExecutionValidationException(
                "SpotTradingCapabilityUnavailable",
                "Execution blocked because spot trading capability is unavailable for the selected Binance account.");
        }
    }

    private async Task<SymbolMetadataSnapshot> ResolveSymbolMetadataAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var cachedMetadata = marketDataService is null
            ? null
            : await marketDataService.GetSymbolMetadataAsync(symbol, cancellationToken);

        if (cachedMetadata is not null)
        {
            return cachedMetadata;
        }

        if (exchangeInfoClient is null)
        {
            throw new ExecutionValidationException(
                "SymbolMetadataUnavailable",
                $"Symbol metadata for '{symbol}' is unavailable.");
        }

        var snapshots = await exchangeInfoClient.GetSymbolMetadataAsync([symbol], cancellationToken);
        var resolvedSnapshot = snapshots.SingleOrDefault();

        return resolvedSnapshot
            ?? throw new ExecutionValidationException(
                "SymbolMetadataUnavailable",
                $"Symbol metadata for '{symbol}' is unavailable.");
    }

    private async Task ValidateBalanceAvailabilityAsync(
        Guid exchangeAccountId,
        ExecutionCommand command,
        SymbolMetadataSnapshot metadata,
        CancellationToken cancellationToken)
    {
        var balance = await dbContext.ExchangeBalances
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.ExchangeAccountId == exchangeAccountId &&
                          entity.Plane == ExchangeDataPlane.Spot &&
                          entity.Asset == ResolveGuardAsset(command, metadata) &&
                          !entity.IsDeleted,
                cancellationToken);

        var availableBalance = ResolveAvailableBalance(balance);
        var requiredBalance = ResolveRequiredBalance(command);

        if (availableBalance + 0.000000000000000001m >= requiredBalance)
        {
            return;
        }

        var asset = ResolveGuardAsset(command, metadata);
        var reasonCode = command.Side == ExecutionOrderSide.Buy
            ? "SpotInsufficientQuoteBalance"
            : "SpotInsufficientBaseAsset";

        throw new ExecutionValidationException(
            reasonCode,
            $"Execution blocked because available {asset} balance {FormatDecimal(availableBalance)} is below required {FormatDecimal(requiredBalance)} for {command.Symbol}.");
    }

    private static void ValidateOrderPreflight(ExecutionCommand command, SymbolMetadataSnapshot metadata)
    {
        if (command.ReduceOnly)
        {
            throw new ExecutionValidationException(
                "SpotReduceOnlyUnsupported",
                $"Execution blocked because reduce-only is not supported for spot order flow on {command.Symbol}.");
        }

        if (!metadata.IsTradingEnabled)
        {
            throw new ExecutionValidationException(
                "SymbolTradingDisabled",
                $"Symbol '{command.Symbol}' is not trading-enabled.");
        }

        if (!string.Equals(metadata.BaseAsset, command.BaseAsset, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(metadata.QuoteAsset, command.QuoteAsset, StringComparison.OrdinalIgnoreCase))
        {
            throw new ExecutionValidationException(
                "SpotSymbolAssetMismatch",
                $"Execution blocked because {command.Symbol} metadata does not match requested base/quote assets.");
        }

        if (metadata.MinQuantity is decimal minQuantity && command.Quantity < minQuantity)
        {
            throw new ExecutionValidationException(
                "OrderQuantityBelowMinimum",
                $"Order quantity {command.Quantity} is below the minimum quantity {minQuantity} for '{command.Symbol}'.");
        }

        if (!IsAligned(command.Quantity, metadata.StepSize))
        {
            throw new ExecutionValidationException(
                "OrderQuantityStepSizeMismatch",
                $"Order quantity {command.Quantity} does not align with step size {metadata.StepSize} for '{command.Symbol}'.");
        }

        if (metadata.QuantityPrecision is int quantityPrecision &&
            CountFractionalDigits(command.Quantity) > quantityPrecision)
        {
            throw new ExecutionValidationException(
                "OrderQuantityPrecisionExceeded",
                $"Order quantity {command.Quantity} exceeds quantity precision {quantityPrecision} for '{command.Symbol}'.");
        }

        var notional = ResolveReferenceNotional(command);
        if (metadata.MinNotional is decimal minNotional &&
            notional < minNotional)
        {
            throw new ExecutionValidationException(
                "OrderNotionalBelowMinimum",
                $"Order notional {notional} is below the minimum notional {minNotional} for '{command.Symbol}'.");
        }

        if (command.OrderType != ExecutionOrderType.Limit)
        {
            if (!string.IsNullOrWhiteSpace(command.TimeInForce))
            {
                throw new ExecutionValidationException(
                    "SpotTimeInForceUnsupported",
                    $"Execution blocked because time-in-force is only supported for limit spot orders on {command.Symbol}.");
            }

            return;
        }

        if (!IsAligned(command.Price, metadata.TickSize))
        {
            throw new ExecutionValidationException(
                "LimitPriceTickSizeMismatch",
                $"Limit price {command.Price} does not align with tick size {metadata.TickSize} for '{command.Symbol}'.");
        }

        if (metadata.PricePrecision is int pricePrecision &&
            CountFractionalDigits(command.Price) > pricePrecision)
        {
            throw new ExecutionValidationException(
                "LimitPricePrecisionExceeded",
                $"Limit price {command.Price} exceeds price precision {pricePrecision} for '{command.Symbol}'.");
        }

        var timeInForce = ResolveTimeInForce(command);
        if (timeInForce is not "GTC" and not "IOC" and not "FOK")
        {
            throw new ExecutionValidationException(
                "SpotTimeInForceInvalid",
                $"Execution blocked because time-in-force '{command.TimeInForce}' is invalid for spot limit order flow.");
        }
    }

    private static string ResolveGuardAsset(ExecutionCommand command, SymbolMetadataSnapshot metadata)
    {
        return command.Side == ExecutionOrderSide.Buy
            ? metadata.QuoteAsset
            : metadata.BaseAsset;
    }

    private static decimal ResolveRequiredBalance(ExecutionCommand command)
    {
        return command.Side == ExecutionOrderSide.Buy
            ? command.QuoteQuantity ?? ResolveReferenceNotional(command)
            : command.Quantity;
    }

    private static bool ShouldRecordBreakerFailure(Exception exception)
    {
        return exception is not ExecutionValidationException validationException ||
               !IsNonDependencyValidationFailure(validationException.ReasonCode);
    }

    private static bool IsNonDependencyValidationFailure(string? reasonCode)
    {
        return reasonCode is "OrderQuantityBelowMinimum" or
            "OrderQuantityStepSizeMismatch" or
            "OrderQuantityPrecisionExceeded" or
            "OrderNotionalBelowMinimum" or
            "LimitPriceTickSizeMismatch" or
            "LimitPricePrecisionExceeded" or
            "SymbolTradingDisabled" or
            "SpotBalanceInsufficient" or
            "SpotReduceOnlyUnsupported";
    }

    private static decimal ResolveReferenceNotional(ExecutionCommand command)
    {
        return command.QuoteQuantity ?? (command.Quantity * command.Price);
    }

    private static decimal ResolveAvailableBalance(ExchangeBalance? balance)
    {
        if (balance is null)
        {
            return 0m;
        }

        if (balance.AvailableBalance.HasValue)
        {
            return Math.Max(0m, balance.AvailableBalance.Value);
        }

        var lockedBalance = balance.LockedBalance ?? 0m;
        return Math.Max(0m, balance.WalletBalance - lockedBalance);
    }

    private static string? ResolveTimeInForce(ExecutionCommand command)
    {
        return command.OrderType == ExecutionOrderType.Limit
            ? string.IsNullOrWhiteSpace(command.TimeInForce)
                ? "GTC"
                : command.TimeInForce.Trim().ToUpperInvariant()
            : null;
    }

    private static string BuildDispatchDetail(BinanceOrderPlacementResult placementResult)
    {
        if (placementResult.Snapshot is null)
        {
            return $"ClientOrderId={placementResult.ClientOrderId}";
        }

        var parts = new List<string>
        {
            $"ClientOrderId={placementResult.ClientOrderId}",
            $"Plane={placementResult.Snapshot.Plane}",
            $"ExchangeStatus={placementResult.Snapshot.Status}",
            $"ExecutedQuantity={FormatDecimal(placementResult.Snapshot.ExecutedQuantity)}",
            $"CumulativeQuoteQuantity={FormatDecimal(placementResult.Snapshot.CumulativeQuoteQuantity)}",
            $"AveragePrice={FormatDecimal(placementResult.Snapshot.AveragePrice)}"
        };

        if (placementResult.Snapshot.TradeId.HasValue)
        {
            parts.Add($"TradeId={placementResult.Snapshot.TradeId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(placementResult.Snapshot.FeeAsset) &&
            placementResult.Snapshot.FeeAmount.HasValue)
        {
            parts.Add($"Fee={placementResult.Snapshot.FeeAsset}:{FormatDecimal(placementResult.Snapshot.FeeAmount.Value)}");
        }

        return string.Join("; ", parts);
    }

    private static bool IsAligned(decimal value, decimal increment)
    {
        if (increment <= 0m)
        {
            return false;
        }

        return value % increment == 0m;
    }

    private static int CountFractionalDigits(decimal value)
    {
        return (decimal.GetBits(value)[3] >> 16) & 0x7F;
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.##################", CultureInfo.InvariantCulture);
    }

    private static string ResolveFailureCode(Exception exception)
    {
        return exception switch
        {
            ExecutionValidationException validationException => validationException.ReasonCode,
            ExecutionGateRejectedException gateRejectedException => gateRejectedException.Reason.ToString(),
            BinanceClockDriftException => nameof(ExecutionGateBlockedReason.ClockDriftExceeded),
            _ => "DispatchFailed"
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }
}


