using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.DemoPortfolio;

public sealed class DemoPortfolioAccountingService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<DemoPortfolioAccountingService> logger) : IDemoPortfolioAccountingService
{
    private const decimal PrecisionEpsilon = 0.000000000000000001m;
    private const decimal DefaultFuturesMaintenanceMarginRate = 0.005m;
    private const decimal DefaultFuturesLiquidationFeeRate = 0.002m;
    private const string PortfolioScopeKey = "portfolio";

    public async Task<DemoPortfolioAccountingResult> SeedWalletAsync(
        DemoWalletSeedRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownerUserId = NormalizeRequired(request.OwnerUserId, nameof(request.OwnerUserId));
        EnsureDemoEnvironment(request.Environment);
        var operationId = NormalizeOperationId(request.OperationId);

        if (await FindTransactionAsync(ownerUserId, operationId, cancellationToken) is DemoLedgerTransaction existingTransaction)
        {
            return await BuildReplayResultAsync(existingTransaction, cancellationToken);
        }

        var asset = NormalizeAsset(request.Asset, nameof(request.Asset));
        var amount = ValidatePositiveAmount(request.Amount, nameof(request.Amount));
        var occurredAtUtc = NormalizeTimestamp(request.OccurredAtUtc);
        var walletCache = new Dictionary<string, DemoWallet>(StringComparer.Ordinal);
        var walletMutations = new Dictionary<string, WalletMutation>(StringComparer.Ordinal);
        var wallet = await GetOrCreateWalletAsync(ownerUserId, asset, walletCache, cancellationToken);

        ApplyWalletDelta(walletMutations, wallet, amount, 0m, occurredAtUtc);

        var transaction = new DemoLedgerTransaction
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            OperationId = operationId,
            TransactionType = DemoLedgerTransactionType.WalletSeeded,
            PositionScopeKey = PortfolioScopeKey,
            OccurredAtUtc = occurredAtUtc
        };

        var entries = CreateEntries(ownerUserId, transaction.Id, walletMutations);
        dbContext.DemoLedgerTransactions.Add(transaction);
        dbContext.DemoLedgerEntries.AddRange(entries);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Demo portfolio transaction {OperationId} applied as {TransactionType}.",
            operationId,
            transaction.TransactionType);

        return BuildResult(transaction, entries, position: null, isReplay: false);
    }

    public async Task<DemoPortfolioAccountingResult> ReserveFundsAsync(
        DemoFundsReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownerUserId = NormalizeRequired(request.OwnerUserId, nameof(request.OwnerUserId));
        EnsureDemoEnvironment(request.Environment);
        var operationId = NormalizeOperationId(request.OperationId);

        if (await FindTransactionAsync(ownerUserId, operationId, cancellationToken) is DemoLedgerTransaction existingTransaction)
        {
            return await BuildReplayResultAsync(existingTransaction, cancellationToken);
        }

        var asset = NormalizeAsset(request.Asset, nameof(request.Asset));
        var amount = ValidatePositiveAmount(request.Amount, nameof(request.Amount));
        var occurredAtUtc = NormalizeTimestamp(request.OccurredAtUtc);
        var walletCache = new Dictionary<string, DemoWallet>(StringComparer.Ordinal);
        var walletMutations = new Dictionary<string, WalletMutation>(StringComparer.Ordinal);
        var wallet = await GetOrCreateWalletAsync(ownerUserId, asset, walletCache, cancellationToken);

        ApplyWalletDelta(walletMutations, wallet, -amount, amount, occurredAtUtc);

        var transaction = new DemoLedgerTransaction
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            OperationId = operationId,
            TransactionType = DemoLedgerTransactionType.FundsReserved,
            PositionScopeKey = PortfolioScopeKey,
            OrderId = NormalizeOptional(request.OrderId),
            OccurredAtUtc = occurredAtUtc
        };

        var entries = CreateEntries(ownerUserId, transaction.Id, walletMutations);
        dbContext.DemoLedgerTransactions.Add(transaction);
        dbContext.DemoLedgerEntries.AddRange(entries);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Demo portfolio transaction {OperationId} applied as {TransactionType}.",
            operationId,
            transaction.TransactionType);

        return BuildResult(transaction, entries, position: null, isReplay: false);
    }

    public async Task<DemoPortfolioAccountingResult> ReleaseFundsAsync(
        DemoFundsReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownerUserId = NormalizeRequired(request.OwnerUserId, nameof(request.OwnerUserId));
        EnsureDemoEnvironment(request.Environment);
        var operationId = NormalizeOperationId(request.OperationId);

        if (await FindTransactionAsync(ownerUserId, operationId, cancellationToken) is DemoLedgerTransaction existingTransaction)
        {
            return await BuildReplayResultAsync(existingTransaction, cancellationToken);
        }

        var asset = NormalizeAsset(request.Asset, nameof(request.Asset));
        var amount = ValidatePositiveAmount(request.Amount, nameof(request.Amount));
        var occurredAtUtc = NormalizeTimestamp(request.OccurredAtUtc);
        var walletCache = new Dictionary<string, DemoWallet>(StringComparer.Ordinal);
        var walletMutations = new Dictionary<string, WalletMutation>(StringComparer.Ordinal);
        var wallet = await GetOrCreateWalletAsync(ownerUserId, asset, walletCache, cancellationToken);

        ApplyWalletDelta(walletMutations, wallet, amount, -amount, occurredAtUtc);

        var transaction = new DemoLedgerTransaction
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            OperationId = operationId,
            TransactionType = DemoLedgerTransactionType.FundsReleased,
            PositionScopeKey = PortfolioScopeKey,
            OrderId = NormalizeOptional(request.OrderId),
            OccurredAtUtc = occurredAtUtc
        };

        var entries = CreateEntries(ownerUserId, transaction.Id, walletMutations);
        dbContext.DemoLedgerTransactions.Add(transaction);
        dbContext.DemoLedgerEntries.AddRange(entries);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Demo portfolio transaction {OperationId} applied as {TransactionType}.",
            operationId,
            transaction.TransactionType);

        return BuildResult(transaction, entries, position: null, isReplay: false);
    }

    public async Task<DemoPortfolioAccountingResult> ApplyFillAsync(
        DemoFillAccountingRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownerUserId = NormalizeRequired(request.OwnerUserId, nameof(request.OwnerUserId));
        EnsureDemoEnvironment(request.Environment);
        var operationId = NormalizeOperationId(request.OperationId);

        if (await FindTransactionAsync(ownerUserId, operationId, cancellationToken) is DemoLedgerTransaction existingTransaction)
        {
            return await BuildReplayResultAsync(existingTransaction, cancellationToken);
        }

        ValidatePositiveAmount(request.Quantity, nameof(request.Quantity));
        ValidatePositiveAmount(request.Price, nameof(request.Price));

        var symbol = NormalizeAsset(request.Symbol, nameof(request.Symbol));
        var baseAsset = NormalizeAsset(request.BaseAsset, nameof(request.BaseAsset));
        var quoteAsset = NormalizeAsset(request.QuoteAsset, nameof(request.QuoteAsset));

        if (string.Equals(baseAsset, quoteAsset, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("BaseAsset and QuoteAsset must differ for demo fills.");
        }

        var bot = request.BotId.HasValue
            ? await LoadBotAsync(ownerUserId, request.BotId.Value, cancellationToken)
            : null;
        var positionScopeKey = CreatePositionScopeKey(request.BotId);
        var occurredAtUtc = NormalizeTimestamp(request.OccurredAtUtc);
        var feeAmount = ValidateNonNegativeAmount(request.FeeAmount, nameof(request.FeeAmount));
        var normalizedFeeAsset = feeAmount == 0m
            ? null
            : NormalizeAsset(request.FeeAsset, nameof(request.FeeAsset));
        var feeAmountInQuote = ResolveFeeAmountInQuote(
            normalizedFeeAsset,
            feeAmount,
            baseAsset,
            quoteAsset,
            request.Price,
            request.FeeAmountInQuote);
        var walletCache = new Dictionary<string, DemoWallet>(StringComparer.Ordinal);
        var walletMutations = new Dictionary<string, WalletMutation>(StringComparer.Ordinal);
        var position = await GetOrCreatePositionAsync(
            ownerUserId,
            request.BotId,
            positionScopeKey,
            symbol,
            baseAsset,
            quoteAsset,
            cancellationToken);
        ConfigurePositionMode(
            position,
            request.PositionKind,
            request.MarginMode,
            request.Leverage,
            request.MaintenanceMarginRate);
        var transaction = CreateFillTransaction(
            ownerUserId,
            operationId,
            positionScopeKey,
            request,
            symbol,
            baseAsset,
            quoteAsset,
            normalizedFeeAsset,
            feeAmount,
            feeAmountInQuote,
            occurredAtUtc);

        if (position.PositionKind == DemoPositionKind.Futures)
        {
            await ApplyFuturesFillAsync(
                request,
                ownerUserId,
                quoteAsset,
                feeAmountInQuote,
                occurredAtUtc,
                walletCache,
                walletMutations,
                position,
                transaction,
                cancellationToken);
        }
        else if (request.Side == DemoTradeSide.Buy)
        {
            await ApplyBuyFillAsync(
                request,
                ownerUserId,
                baseAsset,
                quoteAsset,
                normalizedFeeAsset,
                feeAmount,
                feeAmountInQuote,
                occurredAtUtc,
                walletCache,
                walletMutations,
                position,
                cancellationToken);
        }
        else
        {
            await ApplySellFillAsync(
                request,
                ownerUserId,
                baseAsset,
                quoteAsset,
                normalizedFeeAsset,
                feeAmount,
                feeAmountInQuote,
                occurredAtUtc,
                walletCache,
                walletMutations,
                position,
                transaction,
                cancellationToken);
        }

        if (position.PositionKind == DemoPositionKind.Spot)
        {
            ApplyValuation(position, request.Price, request.MarkPrice ?? request.Price, occurredAtUtc);
        }

        CapturePositionSnapshot(transaction, position);

        var entries = CreateEntries(ownerUserId, transaction.Id, walletMutations);
        dbContext.DemoLedgerTransactions.Add(transaction);
        dbContext.DemoLedgerEntries.AddRange(entries);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (bot is not null)
        {
            await UpdateBotOpenPositionCountAsync(bot, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogDebug(
            "Demo portfolio transaction {OperationId} applied as {TransactionType}.",
            operationId,
            transaction.TransactionType);

        return BuildResult(transaction, entries, position, isReplay: false);
    }

    public async Task<DemoPortfolioAccountingResult> UpdateMarkPriceAsync(
        DemoMarkPriceUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var ownerUserId = NormalizeRequired(request.OwnerUserId, nameof(request.OwnerUserId));
        EnsureDemoEnvironment(request.Environment);
        var operationId = NormalizeOperationId(request.OperationId);

        if (await FindTransactionAsync(ownerUserId, operationId, cancellationToken) is DemoLedgerTransaction existingTransaction)
        {
            return await BuildReplayResultAsync(existingTransaction, cancellationToken);
        }

        ValidatePositiveAmount(request.MarkPrice, nameof(request.MarkPrice));

        var symbol = NormalizeAsset(request.Symbol, nameof(request.Symbol));
        var baseAsset = NormalizeAsset(request.BaseAsset, nameof(request.BaseAsset));
        var quoteAsset = NormalizeAsset(request.QuoteAsset, nameof(request.QuoteAsset));
        var bot = request.BotId.HasValue
            ? await LoadBotAsync(ownerUserId, request.BotId.Value, cancellationToken)
            : null;
        var positionScopeKey = CreatePositionScopeKey(request.BotId);
        var occurredAtUtc = NormalizeTimestamp(request.OccurredAtUtc);
        var position = await dbContext.DemoPositions
            .SingleOrDefaultAsync(
                entity => entity.OwnerUserId == ownerUserId &&
                          entity.PositionScopeKey == positionScopeKey &&
                          entity.Symbol == symbol,
                cancellationToken)
            ?? throw new InvalidOperationException("Mark price update requires an existing demo position.");

        EnsurePositionPair(position, baseAsset, quoteAsset);
        EnsureRequestedPositionKind(position, request.PositionKind);

        var transaction = new DemoLedgerTransaction
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            OperationId = operationId,
            TransactionType = DemoLedgerTransactionType.MarkPriceUpdated,
            BotId = request.BotId,
            PositionScopeKey = positionScopeKey,
            Symbol = symbol,
            BaseAsset = baseAsset,
            QuoteAsset = quoteAsset,
            Price = request.MarkPrice,
            OccurredAtUtc = occurredAtUtc
        };

        if (position.PositionKind == DemoPositionKind.Futures)
        {
            var walletCache = new Dictionary<string, DemoWallet>(StringComparer.Ordinal);
            var walletMutations = new Dictionary<string, WalletMutation>(StringComparer.Ordinal);
            await ApplyFuturesMarkUpdateAsync(
                request,
                ownerUserId,
                occurredAtUtc,
                walletCache,
                walletMutations,
                position,
                transaction,
                cancellationToken);

            var entries = CreateEntries(ownerUserId, transaction.Id, walletMutations);
            dbContext.DemoLedgerTransactions.Add(transaction);
            dbContext.DemoLedgerEntries.AddRange(entries);
            await dbContext.SaveChangesAsync(cancellationToken);

            if (bot is not null)
            {
                await UpdateBotOpenPositionCountAsync(bot, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return BuildResult(transaction, entries, position, isReplay: false);
        }

        ApplyValuation(position, position.LastFillPrice, request.MarkPrice, occurredAtUtc);
        CapturePositionSnapshot(transaction, position);
        dbContext.DemoLedgerTransactions.Add(transaction);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (bot is not null)
        {
            await UpdateBotOpenPositionCountAsync(bot, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return BuildResult(transaction, Array.Empty<DemoLedgerEntry>(), position, isReplay: false);
    }

    private async Task ApplyBuyFillAsync(
        DemoFillAccountingRequest request,
        string ownerUserId,
        string baseAsset,
        string quoteAsset,
        string? feeAsset,
        decimal feeAmount,
        decimal feeAmountInQuote,
        DateTime occurredAtUtc,
        IDictionary<string, DemoWallet> walletCache,
        IDictionary<string, WalletMutation> walletMutations,
        DemoPosition position,
        CancellationToken cancellationToken)
    {
        var quoteWallet = await GetOrCreateWalletAsync(ownerUserId, quoteAsset, walletCache, cancellationToken);
        var baseWallet = await GetOrCreateWalletAsync(ownerUserId, baseAsset, walletCache, cancellationToken);
        var tradeValue = request.Quantity * request.Price;
        var totalQuoteDebit = tradeValue + (feeAsset == quoteAsset ? feeAmount : 0m);
        var consumedReservedAmount = ValidateConsumedReservedAmount(
            request.ConsumedReservedDebitAmount,
            totalQuoteDebit,
            quoteAsset);

        ApplyWalletDelta(walletMutations, quoteWallet, 0m, -consumedReservedAmount, occurredAtUtc);
        ApplyWalletDelta(walletMutations, quoteWallet, -(totalQuoteDebit - consumedReservedAmount), 0m, occurredAtUtc);
        ApplyWalletDelta(
            walletMutations,
            baseWallet,
            request.Quantity - (feeAsset == baseAsset ? feeAmount : 0m),
            0m,
            occurredAtUtc);

        if (feeAsset is not null && feeAsset != quoteAsset && feeAsset != baseAsset)
        {
            var feeWallet = await GetOrCreateWalletAsync(ownerUserId, feeAsset, walletCache, cancellationToken);
            ApplyWalletDelta(walletMutations, feeWallet, -feeAmount, 0m, occurredAtUtc);
        }

        var netBaseQuantity = request.Quantity - (feeAsset == baseAsset ? feeAmount : 0m);

        if (netBaseQuantity <= 0m)
        {
            throw new InvalidOperationException("Buy fill must increase the demo position quantity.");
        }

        var costIncrease = tradeValue + (feeAsset is not null && feeAsset != baseAsset ? feeAmountInQuote : 0m);
        position.Quantity = ClampZero(position.Quantity + netBaseQuantity);
        position.CostBasis = ClampZero(position.CostBasis + costIncrease);
        position.AverageEntryPrice = position.Quantity == 0m
            ? 0m
            : position.CostBasis / position.Quantity;
        position.TotalFeesInQuote = ClampZero(position.TotalFeesInQuote + feeAmountInQuote);
    }

    private async Task ApplySellFillAsync(
        DemoFillAccountingRequest request,
        string ownerUserId,
        string baseAsset,
        string quoteAsset,
        string? feeAsset,
        decimal feeAmount,
        decimal feeAmountInQuote,
        DateTime occurredAtUtc,
        IDictionary<string, DemoWallet> walletCache,
        IDictionary<string, WalletMutation> walletMutations,
        DemoPosition position,
        DemoLedgerTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (position.Quantity <= 0m)
        {
            throw new InvalidOperationException("Sell fill requires an existing demo position.");
        }

        var baseWallet = await GetOrCreateWalletAsync(ownerUserId, baseAsset, walletCache, cancellationToken);
        var quoteWallet = await GetOrCreateWalletAsync(ownerUserId, quoteAsset, walletCache, cancellationToken);
        var tradeValue = request.Quantity * request.Price;
        var totalBaseDebit = request.Quantity + (feeAsset == baseAsset ? feeAmount : 0m);
        var consumedReservedAmount = ValidateConsumedReservedAmount(
            request.ConsumedReservedDebitAmount,
            totalBaseDebit,
            baseAsset);
        var averageCostBefore = position.Quantity == 0m
            ? 0m
            : position.CostBasis / position.Quantity;
        var costRelief = averageCostBefore * totalBaseDebit;
        var netQuoteProceeds = tradeValue - (feeAsset is not null && feeAsset != baseAsset ? feeAmountInQuote : 0m);

        if (position.Quantity + PrecisionEpsilon < totalBaseDebit)
        {
            throw new InvalidOperationException("Sell fill exceeds the available demo position quantity.");
        }

        ApplyWalletDelta(walletMutations, baseWallet, 0m, -consumedReservedAmount, occurredAtUtc);
        ApplyWalletDelta(walletMutations, baseWallet, -(totalBaseDebit - consumedReservedAmount), 0m, occurredAtUtc);
        ApplyWalletDelta(
            walletMutations,
            quoteWallet,
            tradeValue - (feeAsset == quoteAsset ? feeAmount : 0m),
            0m,
            occurredAtUtc);

        if (feeAsset is not null && feeAsset != quoteAsset && feeAsset != baseAsset)
        {
            var feeWallet = await GetOrCreateWalletAsync(ownerUserId, feeAsset, walletCache, cancellationToken);
            ApplyWalletDelta(walletMutations, feeWallet, -feeAmount, 0m, occurredAtUtc);
        }

        position.Quantity = ClampZero(position.Quantity - totalBaseDebit);
        position.CostBasis = position.Quantity == 0m
            ? 0m
            : ClampZero(position.CostBasis - costRelief);
        position.RealizedPnl = ClampZero(position.RealizedPnl + (netQuoteProceeds - costRelief));
        position.AverageEntryPrice = position.Quantity == 0m
            ? 0m
            : position.CostBasis / position.Quantity;
        position.TotalFeesInQuote = ClampZero(position.TotalFeesInQuote + feeAmountInQuote);
        transaction.RealizedPnlDelta = netQuoteProceeds - costRelief;
    }

    private async Task ApplyFuturesFillAsync(
        DemoFillAccountingRequest request,
        string ownerUserId,
        string quoteAsset,
        decimal feeAmountInQuote,
        DateTime occurredAtUtc,
        IDictionary<string, DemoWallet> walletCache,
        IDictionary<string, WalletMutation> walletMutations,
        DemoPosition position,
        DemoLedgerTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (request.ConsumedReservedDebitAmount != 0m)
        {
            throw new InvalidOperationException("Futures demo fills do not consume spot-style reserved balances.");
        }

        var quoteWallet = await GetOrCreateWalletAsync(ownerUserId, quoteAsset, walletCache, cancellationToken);
        var outcome = CalculateFuturesFillOutcome(position, request);

        if (position.MarginMode == DemoMarginMode.Cross)
        {
            ApplyWalletDelta(walletMutations, quoteWallet, outcome.RealizedPnlDelta, 0m, occurredAtUtc);
        }
        else if (outcome.RealizedPnlDelta != 0m)
        {
            ApplyIsolatedMarginDelta(walletMutations, quoteWallet, position, outcome.RealizedPnlDelta, occurredAtUtc);
        }

        if (position.MarginMode == DemoMarginMode.Isolated && outcome.ClosedExistingExposure)
        {
            ReleaseIsolatedMargin(walletMutations, quoteWallet, position, occurredAtUtc);
        }

        position.Quantity = outcome.NewQuantity;
        position.CostBasis = position.Quantity == 0m
            ? 0m
            : ClampZero(Math.Abs(position.Quantity) * outcome.NewAverageEntryPrice);
        position.AverageEntryPrice = outcome.NewAverageEntryPrice;
        position.RealizedPnl = ClampZero(position.RealizedPnl + outcome.RealizedPnlDelta);
        position.TotalFeesInQuote = ClampZero(position.TotalFeesInQuote + feeAmountInQuote);
        position.LastPrice = request.Price;
        position.LastFillPrice = request.Price;
        position.LastFilledAtUtc = occurredAtUtc;
        transaction.RealizedPnlDelta = outcome.RealizedPnlDelta;

        if (position.Quantity == 0m)
        {
            if (feeAmountInQuote != 0m)
            {
                ApplyWalletDelta(walletMutations, quoteWallet, -feeAmountInQuote, 0m, occurredAtUtc);
            }
        }
        else if (position.MarginMode == DemoMarginMode.Isolated)
        {
            EnsureIsolatedMarginRequirement(walletMutations, quoteWallet, position, occurredAtUtc);

            if (feeAmountInQuote != 0m)
            {
                ApplyIsolatedMarginDelta(walletMutations, quoteWallet, position, -feeAmountInQuote, occurredAtUtc);
                EnsureIsolatedMarginRequirement(walletMutations, quoteWallet, position, occurredAtUtc);
            }
        }
        else
        {
            if (feeAmountInQuote != 0m)
            {
                ApplyWalletDelta(walletMutations, quoteWallet, -feeAmountInQuote, 0m, occurredAtUtc);
            }

            await EnsureCrossMarginCapacityAsync(
                ownerUserId,
                quoteAsset,
                position,
                quoteWallet,
                cancellationToken);
        }

        if (request.FundingRate is decimal fundingRate && fundingRate != 0m && position.Quantity != 0m)
        {
            ApplyFundingDelta(
                walletMutations,
                quoteWallet,
                position,
                fundingRate,
                request.MarkPrice ?? request.Price,
                occurredAtUtc,
                transaction);
        }

        await ApplyFuturesValuationAndControlsAsync(
            ownerUserId,
            quoteWallet,
            position,
            fillPrice: request.Price,
            lastPrice: request.Price,
            valuationPrice: request.MarkPrice ?? request.Price,
            occurredAtUtc,
            walletCache,
            walletMutations,
            transaction,
            cancellationToken);
    }

    private async Task ApplyFuturesMarkUpdateAsync(
        DemoMarkPriceUpdateRequest request,
        string ownerUserId,
        DateTime occurredAtUtc,
        IDictionary<string, DemoWallet> walletCache,
        IDictionary<string, WalletMutation> walletMutations,
        DemoPosition position,
        DemoLedgerTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (request.LastPrice is decimal lastPrice)
        {
            ValidatePositiveAmount(lastPrice, nameof(request.LastPrice));
        }

        var quoteWallet = await GetOrCreateWalletAsync(ownerUserId, position.QuoteAsset, walletCache, cancellationToken);

        if (request.FundingRate is decimal fundingRate && fundingRate != 0m && position.Quantity != 0m)
        {
            ApplyFundingDelta(
                walletMutations,
                quoteWallet,
                position,
                fundingRate,
                request.MarkPrice,
                occurredAtUtc,
                transaction);
        }

        await ApplyFuturesValuationAndControlsAsync(
            ownerUserId,
            quoteWallet,
            position,
            fillPrice: null,
            lastPrice: request.LastPrice,
            valuationPrice: request.MarkPrice,
            occurredAtUtc,
            walletCache,
            walletMutations,
            transaction,
            cancellationToken);
    }

    private async Task ApplyFuturesValuationAndControlsAsync(
        string ownerUserId,
        DemoWallet quoteWallet,
        DemoPosition position,
        decimal? fillPrice,
        decimal? lastPrice,
        decimal valuationPrice,
        DateTime occurredAtUtc,
        IDictionary<string, DemoWallet> walletCache,
        IDictionary<string, WalletMutation> walletMutations,
        DemoLedgerTransaction transaction,
        CancellationToken cancellationToken)
    {
        ApplyFuturesValuation(position, fillPrice, lastPrice, valuationPrice, occurredAtUtc);
        await RefreshFuturesRiskMetricsAsync(ownerUserId, quoteWallet, position, cancellationToken);

        if (ShouldLiquidate(position))
        {
            LiquidateFuturesPosition(
                walletMutations,
                quoteWallet,
                position,
                occurredAtUtc,
                transaction);

            ApplyFuturesValuation(
                position,
                fillPrice: null,
                lastPrice: lastPrice,
                valuationPrice: valuationPrice,
                occurredAtUtc: occurredAtUtc);
            await RefreshFuturesRiskMetricsAsync(ownerUserId, quoteWallet, position, cancellationToken);
        }
    }

    private static void ApplyFuturesValuation(
        DemoPosition position,
        decimal? fillPrice,
        decimal? lastPrice,
        decimal valuationPrice,
        DateTime occurredAtUtc)
    {
        position.LastMarkPrice = valuationPrice;
        position.LastPrice = lastPrice ?? position.LastPrice ?? fillPrice ?? valuationPrice;
        position.LastValuationAtUtc = occurredAtUtc;

        if (fillPrice.HasValue)
        {
            position.LastFillPrice = fillPrice.Value;
            position.LastFilledAtUtc = occurredAtUtc;
        }

        position.UnrealizedPnl = position.Quantity == 0m
            ? 0m
            : ClampZero((valuationPrice - position.AverageEntryPrice) * position.Quantity);
    }

    private void ApplyFundingDelta(
        IDictionary<string, WalletMutation> walletMutations,
        DemoWallet quoteWallet,
        DemoPosition position,
        decimal fundingRate,
        decimal markPrice,
        DateTime occurredAtUtc,
        DemoLedgerTransaction transaction)
    {
        var fundingDelta = CalculateFundingDelta(position.Quantity, markPrice, fundingRate);

        if (fundingDelta == 0m)
        {
            return;
        }

        if (position.MarginMode == DemoMarginMode.Isolated)
        {
            ApplyIsolatedMarginDelta(walletMutations, quoteWallet, position, fundingDelta, occurredAtUtc);
        }
        else
        {
            ApplyWalletDelta(walletMutations, quoteWallet, fundingDelta, 0m, occurredAtUtc);
        }

        position.NetFundingInQuote = ClampZero(position.NetFundingInQuote + fundingDelta);
        position.LastFundingRate = fundingRate;
        position.LastFundingAppliedAtUtc = occurredAtUtc;
        transaction.FundingRate = fundingRate;
        transaction.FundingDeltaInQuote = ClampZero((transaction.FundingDeltaInQuote ?? 0m) + fundingDelta);
    }

    private async Task RefreshFuturesRiskMetricsAsync(
        string ownerUserId,
        DemoWallet quoteWallet,
        DemoPosition position,
        CancellationToken cancellationToken)
    {
        if (position.Quantity == 0m)
        {
            position.MaintenanceMargin = 0m;
            position.MarginBalance = 0m;
            position.LiquidationPrice = null;
            return;
        }

        var maintenanceMarginRate = position.MaintenanceMarginRate ?? DefaultFuturesMaintenanceMarginRate;
        var markPrice = position.LastMarkPrice ?? position.LastPrice ?? position.AverageEntryPrice;
        position.MaintenanceMarginRate = maintenanceMarginRate;
        position.MaintenanceMargin = CalculateMaintenanceMargin(position.Quantity, markPrice, maintenanceMarginRate);

        if (position.MarginMode == DemoMarginMode.Isolated)
        {
            position.MarginBalance = ClampZero(position.IsolatedMargin.GetValueOrDefault() + position.UnrealizedPnl);
            position.LiquidationPrice = CalculateIsolatedLiquidationPrice(position);
            return;
        }

        var otherCrossPositions = await dbContext.DemoPositions
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted &&
                entity.PositionKind == DemoPositionKind.Futures &&
                entity.MarginMode == DemoMarginMode.Cross &&
                entity.QuoteAsset == position.QuoteAsset &&
                entity.Quantity != 0m &&
                !(entity.PositionScopeKey == position.PositionScopeKey && entity.Symbol == position.Symbol))
            .ToListAsync(cancellationToken);

        var otherUnrealizedPnl = otherCrossPositions.Sum(entity => entity.UnrealizedPnl);
        var otherMaintenanceMargin = otherCrossPositions.Sum(entity =>
            entity.MaintenanceMargin ??
            CalculateMaintenanceMargin(
                entity.Quantity,
                entity.LastMarkPrice ?? entity.LastPrice ?? entity.AverageEntryPrice,
                entity.MaintenanceMarginRate ?? DefaultFuturesMaintenanceMarginRate));

        position.MarginBalance = ClampZero(quoteWallet.AvailableBalance + otherUnrealizedPnl + position.UnrealizedPnl);
        position.LiquidationPrice = CalculateCrossLiquidationPrice(
            position,
            quoteWallet.AvailableBalance,
            otherUnrealizedPnl,
            otherMaintenanceMargin);
    }

    private async Task EnsureCrossMarginCapacityAsync(
        string ownerUserId,
        string quoteAsset,
        DemoPosition targetPosition,
        DemoWallet quoteWallet,
        CancellationToken cancellationToken)
    {
        if (targetPosition.Quantity == 0m)
        {
            return;
        }

        var otherCrossPositions = await dbContext.DemoPositions
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted &&
                entity.PositionKind == DemoPositionKind.Futures &&
                entity.MarginMode == DemoMarginMode.Cross &&
                entity.QuoteAsset == quoteAsset &&
                entity.Quantity != 0m &&
                !(entity.PositionScopeKey == targetPosition.PositionScopeKey && entity.Symbol == targetPosition.Symbol))
            .ToListAsync(cancellationToken);

        var requiredInitialMargin = otherCrossPositions.Sum(entity =>
                CalculateInitialMargin(entity.Quantity, entity.AverageEntryPrice, entity.Leverage ?? 1m))
            + CalculateInitialMargin(targetPosition.Quantity, targetPosition.AverageEntryPrice, targetPosition.Leverage ?? 1m);

        if (quoteWallet.AvailableBalance + PrecisionEpsilon < requiredInitialMargin)
        {
            throw new InvalidOperationException(
                $"Cross margin collateral is insufficient for futures symbol '{targetPosition.Symbol}'.");
        }
    }

    private static void EnsureIsolatedMarginRequirement(
        IDictionary<string, WalletMutation> walletMutations,
        DemoWallet quoteWallet,
        DemoPosition position,
        DateTime occurredAtUtc)
    {
        if (position.Quantity == 0m)
        {
            return;
        }

        var leverage = position.Leverage ?? 1m;
        var requiredInitialMargin = CalculateInitialMargin(position.Quantity, position.AverageEntryPrice, leverage);
        var currentIsolatedMargin = position.IsolatedMargin.GetValueOrDefault();

        if (currentIsolatedMargin + PrecisionEpsilon >= requiredInitialMargin)
        {
            return;
        }

        var topUpAmount = ClampZero(requiredInitialMargin - currentIsolatedMargin);
        ApplyWalletDelta(walletMutations, quoteWallet, -topUpAmount, topUpAmount, occurredAtUtc);
        position.IsolatedMargin = ClampZero(currentIsolatedMargin + topUpAmount);
    }

    private static void ApplyIsolatedMarginDelta(
        IDictionary<string, WalletMutation> walletMutations,
        DemoWallet quoteWallet,
        DemoPosition position,
        decimal marginDelta,
        DateTime occurredAtUtc)
    {
        if (marginDelta == 0m)
        {
            return;
        }

        var nextIsolatedMargin = position.IsolatedMargin.GetValueOrDefault() + marginDelta;

        if (nextIsolatedMargin + PrecisionEpsilon < 0m)
        {
            throw new InvalidOperationException(
                $"Isolated futures margin is exhausted for symbol '{position.Symbol}'.");
        }

        ApplyWalletDelta(walletMutations, quoteWallet, 0m, marginDelta, occurredAtUtc);
        position.IsolatedMargin = ClampZero(nextIsolatedMargin);
    }

    private static void ReleaseIsolatedMargin(
        IDictionary<string, WalletMutation> walletMutations,
        DemoWallet quoteWallet,
        DemoPosition position,
        DateTime occurredAtUtc)
    {
        var isolatedMargin = position.IsolatedMargin.GetValueOrDefault();

        if (isolatedMargin == 0m)
        {
            return;
        }

        ApplyWalletDelta(walletMutations, quoteWallet, isolatedMargin, -isolatedMargin, occurredAtUtc);
        position.IsolatedMargin = 0m;
    }

    private static void LiquidateFuturesPosition(
        IDictionary<string, WalletMutation> walletMutations,
        DemoWallet quoteWallet,
        DemoPosition position,
        DateTime occurredAtUtc,
        DemoLedgerTransaction transaction)
    {
        var liquidationPrice = position.LiquidationPrice ?? position.LastMarkPrice ?? position.AverageEntryPrice;
        var liquidationFee = ClampZero(Math.Abs(position.Quantity) * liquidationPrice * DefaultFuturesLiquidationFeeRate);
        var realizedPnlDelta = CalculateCloseRealizedPnl(position.Quantity, position.AverageEntryPrice, liquidationPrice);

        if (position.MarginMode == DemoMarginMode.Isolated)
        {
            var isolatedMargin = position.IsolatedMargin.GetValueOrDefault();
            var netReleaseAmount = ClampZero(isolatedMargin + realizedPnlDelta - liquidationFee);

            if (isolatedMargin != 0m)
            {
                ApplyWalletDelta(walletMutations, quoteWallet, netReleaseAmount, -isolatedMargin, occurredAtUtc);
            }

            position.IsolatedMargin = 0m;
        }
        else
        {
            ApplyWalletDelta(walletMutations, quoteWallet, realizedPnlDelta - liquidationFee, 0m, occurredAtUtc);
        }

        position.RealizedPnl = ClampZero(position.RealizedPnl + realizedPnlDelta);
        position.TotalFeesInQuote = ClampZero(position.TotalFeesInQuote + liquidationFee);
        position.Quantity = 0m;
        position.CostBasis = 0m;
        position.AverageEntryPrice = 0m;
        position.UnrealizedPnl = 0m;
        position.MaintenanceMargin = 0m;
        position.MarginBalance = 0m;
        position.LiquidationPrice = null;

        transaction.TransactionType = DemoLedgerTransactionType.Liquidated;
        transaction.Price = liquidationPrice;
        transaction.RealizedPnlDelta = ClampZero((transaction.RealizedPnlDelta ?? 0m) + realizedPnlDelta);
        transaction.FeeAsset ??= position.QuoteAsset;
        transaction.FeeAmountInQuote = ClampZero((transaction.FeeAmountInQuote ?? 0m) + liquidationFee);
    }

    private static FuturesFillOutcome CalculateFuturesFillOutcome(DemoPosition position, DemoFillAccountingRequest request)
    {
        var signedFillQuantity = request.Side == DemoTradeSide.Buy
            ? request.Quantity
            : -request.Quantity;
        var existingQuantity = position.Quantity;
        var newQuantity = ClampZero(existingQuantity + signedFillQuantity);
        var closingQuantity = existingQuantity == 0m || Math.Sign(existingQuantity) == Math.Sign(signedFillQuantity)
            ? 0m
            : Math.Min(Math.Abs(existingQuantity), Math.Abs(signedFillQuantity));
        var realizedPnlDelta = closingQuantity == 0m
            ? 0m
            : ClampZero((request.Price - position.AverageEntryPrice) * closingQuantity * Math.Sign(existingQuantity));

        decimal newAverageEntryPrice;

        if (newQuantity == 0m)
        {
            newAverageEntryPrice = 0m;
        }
        else if (existingQuantity == 0m || Math.Sign(existingQuantity) == Math.Sign(signedFillQuantity))
        {
            var totalNotional = (Math.Abs(existingQuantity) * position.AverageEntryPrice) + (request.Quantity * request.Price);
            newAverageEntryPrice = totalNotional / Math.Abs(newQuantity);
        }
        else if (Math.Sign(newQuantity) == Math.Sign(existingQuantity))
        {
            newAverageEntryPrice = position.AverageEntryPrice;
        }
        else
        {
            newAverageEntryPrice = request.Price;
        }

        return new FuturesFillOutcome(
            newQuantity,
            ClampZero(newAverageEntryPrice),
            realizedPnlDelta,
            ClosedExistingExposure: existingQuantity != 0m &&
                                    (newQuantity == 0m || Math.Sign(newQuantity) != Math.Sign(existingQuantity)));
    }

    private static DemoLedgerTransaction CreateFillTransaction(
        string ownerUserId,
        string operationId,
        string positionScopeKey,
        DemoFillAccountingRequest request,
        string symbol,
        string baseAsset,
        string quoteAsset,
        string? feeAsset,
        decimal feeAmount,
        decimal feeAmountInQuote,
        DateTime occurredAtUtc)
    {
        return new DemoLedgerTransaction
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            OperationId = operationId,
            TransactionType = DemoLedgerTransactionType.FillApplied,
            BotId = request.BotId,
            PositionScopeKey = positionScopeKey,
            OrderId = NormalizeOptional(request.OrderId),
            FillId = NormalizeOptional(request.FillId),
            Symbol = symbol,
            BaseAsset = baseAsset,
            QuoteAsset = quoteAsset,
            PositionKind = request.PositionKind,
            MarginMode = request.MarginMode,
            Side = request.Side,
            Quantity = request.Quantity,
            Price = request.Price,
            FeeAsset = feeAsset,
            FeeAmount = feeAmount == 0m ? null : feeAmount,
            FeeAmountInQuote = feeAmount == 0m ? null : feeAmountInQuote,
            Leverage = request.Leverage,
            FundingRate = request.FundingRate,
            OccurredAtUtc = occurredAtUtc
        };
    }

    private async Task<DemoPortfolioAccountingResult> BuildReplayResultAsync(
        DemoLedgerTransaction transaction,
        CancellationToken cancellationToken)
    {
        var entries = await dbContext.DemoLedgerEntries
            .AsNoTracking()
            .Where(entity => entity.DemoLedgerTransactionId == transaction.Id)
            .OrderBy(entity => entity.Asset)
            .ToListAsync(cancellationToken);

        return BuildResult(transaction, entries, position: null, isReplay: true);
    }

    private static DemoPortfolioAccountingResult BuildResult(
        DemoLedgerTransaction transaction,
        IReadOnlyCollection<DemoLedgerEntry> entries,
        DemoPosition? position,
        bool isReplay)
    {
        var entrySnapshots = entries
            .OrderBy(entity => entity.Asset, StringComparer.Ordinal)
            .Select(entity => new DemoLedgerEntrySnapshot(
                entity.Asset,
                entity.AvailableDelta,
                entity.ReservedDelta,
                entity.AvailableBalanceAfter,
                entity.ReservedBalanceAfter))
            .ToArray();
        var walletSnapshots = entrySnapshots
            .Select(entry => new DemoWalletBalanceSnapshot(
                entry.Asset,
                entry.AvailableBalanceAfter,
                entry.ReservedBalanceAfter))
            .ToArray();
        var transactionSnapshot = new DemoLedgerTransactionSnapshot(
            transaction.Id,
            transaction.OperationId,
            transaction.TransactionType,
            transaction.BotId,
            transaction.PositionScopeKey,
            transaction.OrderId,
            transaction.FillId,
            transaction.Symbol,
            transaction.BaseAsset,
            transaction.QuoteAsset,
            transaction.Side,
            transaction.Quantity,
            transaction.Price,
            transaction.FeeAsset,
            transaction.FeeAmount,
            transaction.FeeAmountInQuote,
            transaction.RealizedPnlDelta,
            transaction.PositionQuantityAfter,
            transaction.PositionCostBasisAfter,
            transaction.PositionAverageEntryPriceAfter,
            transaction.CumulativeRealizedPnlAfter,
            transaction.UnrealizedPnlAfter,
            transaction.CumulativeFeesInQuoteAfter,
            transaction.MarkPriceAfter,
            transaction.OccurredAtUtc,
            entrySnapshots,
            transaction.PositionKind,
            transaction.MarginMode,
            transaction.Leverage,
            transaction.FundingRate,
            transaction.FundingDeltaInQuote,
            transaction.NetFundingInQuoteAfter,
            transaction.LastPriceAfter,
            transaction.MaintenanceMarginRateAfter,
            transaction.MaintenanceMarginAfter,
            transaction.MarginBalanceAfter,
            transaction.LiquidationPriceAfter);

        return new DemoPortfolioAccountingResult(
            transactionSnapshot,
            position is not null ? MapPositionSnapshot(position) : MapPositionSnapshot(transaction),
            walletSnapshots,
            isReplay);
    }

    private static DemoPositionSnapshot? MapPositionSnapshot(DemoLedgerTransaction transaction)
    {
        if (string.IsNullOrWhiteSpace(transaction.Symbol) ||
            string.IsNullOrWhiteSpace(transaction.BaseAsset) ||
            string.IsNullOrWhiteSpace(transaction.QuoteAsset) ||
            transaction.PositionQuantityAfter is null ||
            transaction.PositionCostBasisAfter is null ||
            transaction.PositionAverageEntryPriceAfter is null ||
            transaction.CumulativeRealizedPnlAfter is null ||
            transaction.UnrealizedPnlAfter is null ||
            transaction.CumulativeFeesInQuoteAfter is null)
        {
            return null;
        }

        return new DemoPositionSnapshot(
            transaction.BotId,
            transaction.PositionScopeKey,
            transaction.Symbol,
            transaction.BaseAsset,
            transaction.QuoteAsset,
            transaction.PositionQuantityAfter.Value,
            transaction.PositionCostBasisAfter.Value,
            transaction.PositionAverageEntryPriceAfter.Value,
            transaction.CumulativeRealizedPnlAfter.Value,
            transaction.UnrealizedPnlAfter.Value,
            transaction.CumulativeFeesInQuoteAfter.Value,
            transaction.MarkPriceAfter,
            transaction.TransactionType == DemoLedgerTransactionType.FillApplied ? transaction.Price : null,
            transaction.TransactionType == DemoLedgerTransactionType.FillApplied ? transaction.OccurredAtUtc : null,
            transaction.MarkPriceAfter.HasValue ? transaction.OccurredAtUtc : null,
            transaction.PositionKind ?? DemoPositionKind.Spot,
            transaction.MarginMode,
            transaction.Leverage,
            transaction.LastPriceAfter,
            transaction.MaintenanceMarginRateAfter,
            transaction.MaintenanceMarginAfter,
            transaction.MarginBalanceAfter,
            transaction.NetFundingInQuoteAfter ?? 0m,
            transaction.LiquidationPriceAfter);
    }

    private static DemoPositionSnapshot MapPositionSnapshot(DemoPosition position)
    {
        return new DemoPositionSnapshot(
            position.BotId,
            position.PositionScopeKey,
            position.Symbol,
            position.BaseAsset,
            position.QuoteAsset,
            position.Quantity,
            position.CostBasis,
            position.AverageEntryPrice,
            position.RealizedPnl,
            position.UnrealizedPnl,
            position.TotalFeesInQuote,
            position.LastMarkPrice,
            position.LastFillPrice,
            position.LastFilledAtUtc,
            position.LastValuationAtUtc,
            position.PositionKind,
            position.MarginMode,
            position.Leverage,
            position.LastPrice,
            position.MaintenanceMarginRate,
            position.MaintenanceMargin,
            position.MarginBalance,
            position.NetFundingInQuote,
            position.LiquidationPrice);
    }

    private async Task<DemoLedgerTransaction?> FindTransactionAsync(
        string ownerUserId,
        string operationId,
        CancellationToken cancellationToken)
    {
        return await dbContext.DemoLedgerTransactions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.OwnerUserId == ownerUserId &&
                          entity.OperationId == operationId,
                cancellationToken);
    }

    private async Task<DemoWallet> GetOrCreateWalletAsync(
        string ownerUserId,
        string asset,
        IDictionary<string, DemoWallet> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(asset, out var cachedWallet))
        {
            return cachedWallet;
        }

        var wallet = await dbContext.DemoWallets
            .SingleOrDefaultAsync(
                entity => entity.OwnerUserId == ownerUserId &&
                          entity.Asset == asset,
                cancellationToken);

        if (wallet is null)
        {
            wallet = new DemoWallet
            {
                OwnerUserId = ownerUserId,
                Asset = asset
            };

            dbContext.DemoWallets.Add(wallet);
        }

        cache[asset] = wallet;
        return wallet;
    }

    private async Task<DemoPosition> GetOrCreatePositionAsync(
        string ownerUserId,
        Guid? botId,
        string positionScopeKey,
        string symbol,
        string baseAsset,
        string quoteAsset,
        CancellationToken cancellationToken)
    {
        var position = await dbContext.DemoPositions
            .SingleOrDefaultAsync(
                entity => entity.OwnerUserId == ownerUserId &&
                          entity.PositionScopeKey == positionScopeKey &&
                          entity.Symbol == symbol,
                cancellationToken);

        if (position is null)
        {
            position = new DemoPosition
            {
                OwnerUserId = ownerUserId,
                BotId = botId,
                PositionScopeKey = positionScopeKey,
                Symbol = symbol,
                BaseAsset = baseAsset,
                QuoteAsset = quoteAsset
            };

            dbContext.DemoPositions.Add(position);
            return position;
        }

        EnsurePositionPair(position, baseAsset, quoteAsset);
        return position;
    }

    private async Task<TradingBot> LoadBotAsync(
        string ownerUserId,
        Guid botId,
        CancellationToken cancellationToken)
    {
        var bot = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(entity => entity.Id == botId && !entity.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Demo accounting could not resolve bot '{botId}'.");

        if (!string.Equals(bot.OwnerUserId, ownerUserId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Demo accounting bot scope does not match the requested owner.");
        }

        return bot;
    }

    private async Task UpdateBotOpenPositionCountAsync(TradingBot bot, CancellationToken cancellationToken)
    {
        bot.OpenPositionCount = await dbContext.DemoPositions
            .CountAsync(
                entity => entity.BotId == bot.Id &&
                          entity.Quantity != 0m,
                cancellationToken);
    }

    private static IReadOnlyCollection<DemoLedgerEntry> CreateEntries(
        string ownerUserId,
        Guid transactionId,
        IReadOnlyDictionary<string, WalletMutation> walletMutations)
    {
        return walletMutations.Values
            .OrderBy(mutation => mutation.Wallet.Asset, StringComparer.Ordinal)
            .Select(mutation => new DemoLedgerEntry
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                DemoLedgerTransactionId = transactionId,
                Asset = mutation.Wallet.Asset,
                AvailableDelta = mutation.AvailableDelta,
                ReservedDelta = mutation.ReservedDelta,
                AvailableBalanceAfter = mutation.Wallet.AvailableBalance,
                ReservedBalanceAfter = mutation.Wallet.ReservedBalance
            })
            .ToArray();
    }

    private static void ApplyWalletDelta(
        IDictionary<string, WalletMutation> walletMutations,
        DemoWallet wallet,
        decimal availableDelta,
        decimal reservedDelta,
        DateTime occurredAtUtc)
    {
        if (!walletMutations.TryGetValue(wallet.Asset, out var mutation))
        {
            mutation = new WalletMutation(wallet);
            walletMutations[wallet.Asset] = mutation;
        }

        mutation.Apply(availableDelta, reservedDelta);
        wallet.LastActivityAtUtc = occurredAtUtc;
    }

    private static void ApplyValuation(
        DemoPosition position,
        decimal? fillPrice,
        decimal valuationPrice,
        DateTime occurredAtUtc)
    {
        position.LastFillPrice = fillPrice;
        position.LastMarkPrice = valuationPrice;
        position.LastFilledAtUtc = fillPrice.HasValue ? occurredAtUtc : position.LastFilledAtUtc;
        position.LastValuationAtUtc = occurredAtUtc;
        position.UnrealizedPnl = position.Quantity == 0m
            ? 0m
            : ClampZero((valuationPrice - position.AverageEntryPrice) * position.Quantity);
    }

    private static void CapturePositionSnapshot(DemoLedgerTransaction transaction, DemoPosition position)
    {
        transaction.PositionKind = position.PositionKind;
        transaction.MarginMode = position.MarginMode;
        transaction.Leverage = position.Leverage;
        transaction.PositionQuantityAfter = position.Quantity;
        transaction.PositionCostBasisAfter = position.CostBasis;
        transaction.PositionAverageEntryPriceAfter = position.AverageEntryPrice;
        transaction.CumulativeRealizedPnlAfter = position.RealizedPnl;
        transaction.UnrealizedPnlAfter = position.UnrealizedPnl;
        transaction.CumulativeFeesInQuoteAfter = position.TotalFeesInQuote;
        transaction.NetFundingInQuoteAfter = position.NetFundingInQuote;
        transaction.LastPriceAfter = position.LastPrice;
        transaction.MarkPriceAfter = position.LastMarkPrice;
        transaction.MaintenanceMarginRateAfter = position.MaintenanceMarginRate;
        transaction.MaintenanceMarginAfter = position.MaintenanceMargin;
        transaction.MarginBalanceAfter = position.MarginBalance;
        transaction.LiquidationPriceAfter = position.LiquidationPrice;
    }

    private static void EnsurePositionPair(DemoPosition position, string baseAsset, string quoteAsset)
    {
        if (!string.Equals(position.BaseAsset, baseAsset, StringComparison.Ordinal) ||
            !string.Equals(position.QuoteAsset, quoteAsset, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Demo position pair mismatch detected for the requested symbol.");
        }
    }

    private static void ConfigurePositionMode(
        DemoPosition position,
        DemoPositionKind requestedPositionKind,
        DemoMarginMode? requestedMarginMode,
        decimal? requestedLeverage,
        decimal? requestedMaintenanceMarginRate)
    {
        if (position.Quantity != 0m && position.PositionKind != requestedPositionKind)
        {
            throw new InvalidOperationException(
                $"Demo position kind cannot change while symbol '{position.Symbol}' still has exposure.");
        }

        if (requestedPositionKind == DemoPositionKind.Spot)
        {
            if (position.Quantity == 0m)
            {
                position.PositionKind = DemoPositionKind.Spot;
                position.MarginMode = null;
                position.Leverage = null;
                position.IsolatedMargin = null;
                position.MaintenanceMarginRate = null;
                position.MaintenanceMargin = null;
                position.MarginBalance = null;
                position.LiquidationPrice = null;
                position.NetFundingInQuote = 0m;
                position.LastFundingRate = null;
                position.LastFundingAppliedAtUtc = null;
            }

            return;
        }

        var leverage = ResolveFuturesLeverage(position, requestedLeverage);
        var maintenanceMarginRate = ResolveMaintenanceMarginRate(position, requestedMaintenanceMarginRate);
        var marginMode = position.MarginMode ?? requestedMarginMode
            ?? throw new InvalidOperationException("Futures demo fills require an explicit margin mode.");

        if (position.Quantity != 0m && position.MarginMode != marginMode)
        {
            throw new InvalidOperationException(
                $"Futures margin mode cannot change while symbol '{position.Symbol}' still has exposure.");
        }

        if (position.Quantity != 0m &&
            position.Leverage.HasValue &&
            position.Leverage.Value != leverage)
        {
            throw new InvalidOperationException(
                $"Futures leverage cannot change while symbol '{position.Symbol}' still has exposure.");
        }

        position.PositionKind = DemoPositionKind.Futures;
        position.MarginMode = marginMode;
        position.Leverage = leverage;
        position.MaintenanceMarginRate = maintenanceMarginRate;
    }

    private static void EnsureRequestedPositionKind(DemoPosition position, DemoPositionKind requestedPositionKind)
    {
        if (position.PositionKind != requestedPositionKind)
        {
            throw new InvalidOperationException(
                $"Demo position kind mismatch detected for symbol '{position.Symbol}'.");
        }
    }

    private DateTime NormalizeTimestamp(DateTime? timestamp)
    {
        var value = timestamp ?? timeProvider.GetUtcNow().UtcDateTime;

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string CreatePositionScopeKey(Guid? botId)
    {
        return botId.HasValue
            ? $"bot:{botId.Value:N}"
            : PortfolioScopeKey;
    }

    private static decimal ResolveFeeAmountInQuote(
        string? feeAsset,
        decimal feeAmount,
        string baseAsset,
        string quoteAsset,
        decimal tradePrice,
        decimal? explicitFeeAmountInQuote)
    {
        if (feeAmount == 0m || string.IsNullOrWhiteSpace(feeAsset))
        {
            return 0m;
        }

        if (string.Equals(feeAsset, quoteAsset, StringComparison.Ordinal))
        {
            return feeAmount;
        }

        if (string.Equals(feeAsset, baseAsset, StringComparison.Ordinal))
        {
            return feeAmount * tradePrice;
        }

        if (explicitFeeAmountInQuote is null || explicitFeeAmountInQuote <= 0m)
        {
            throw new InvalidOperationException("Non-quote demo fees require FeeAmountInQuote for deterministic accounting.");
        }

        return explicitFeeAmountInQuote.Value;
    }

    private static decimal ValidateConsumedReservedAmount(
        decimal consumedReservedAmount,
        decimal maxAmount,
        string asset)
    {
        ValidateNonNegativeAmount(consumedReservedAmount, nameof(consumedReservedAmount));

        if (consumedReservedAmount - maxAmount > PrecisionEpsilon)
        {
            throw new InvalidOperationException(
                $"Consumed reserved amount for asset '{asset}' exceeds the debit required by the fill.");
        }

        return ClampZero(consumedReservedAmount);
    }

    private static void EnsureDemoEnvironment(ExecutionEnvironment environment)
    {
        if (environment != ExecutionEnvironment.Demo)
        {
            throw new InvalidOperationException("Demo portfolio accounting is only available for the Demo execution environment.");
        }
    }

    private static decimal ValidatePositiveAmount(decimal amount, string parameterName)
    {
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The value must be greater than zero.");
        }

        return amount;
    }

    private static decimal ValidateNonNegativeAmount(decimal amount, string parameterName)
    {
        if (amount < 0m)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The value cannot be negative.");
        }

        return amount;
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = NormalizeOptional(value);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalized;
    }

    private static string NormalizeOperationId(string? value)
    {
        var normalized = NormalizeRequired(value, nameof(value));

        if (normalized.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "OperationId cannot exceed 128 characters.");
        }

        return normalized;
    }

    private static string NormalizeAsset(string? value, string parameterName)
    {
        var normalized = NormalizeRequired(value, parameterName).ToUpperInvariant();

        if (normalized.Length > 32)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Asset and symbol codes cannot exceed 32 characters.");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static decimal ResolveFuturesLeverage(DemoPosition position, decimal? requestedLeverage)
    {
        var leverage = position.Leverage ?? requestedLeverage
            ?? throw new InvalidOperationException("Futures demo fills require leverage to be specified.");

        if (leverage < 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedLeverage), "Futures leverage must be at least 1.");
        }

        return leverage;
    }

    private static decimal ResolveMaintenanceMarginRate(DemoPosition position, decimal? requestedMaintenanceMarginRate)
    {
        var maintenanceMarginRate = position.MaintenanceMarginRate
            ?? requestedMaintenanceMarginRate
            ?? DefaultFuturesMaintenanceMarginRate;

        if (maintenanceMarginRate <= 0m || maintenanceMarginRate >= 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedMaintenanceMarginRate),
                "MaintenanceMarginRate must be between 0 and 1 for futures demo positions.");
        }

        return maintenanceMarginRate;
    }

    private static decimal CalculateFundingDelta(decimal quantity, decimal markPrice, decimal fundingRate)
    {
        return ClampZero(-Math.Sign(quantity) * Math.Abs(quantity) * markPrice * fundingRate);
    }

    private static decimal CalculateInitialMargin(decimal quantity, decimal entryPrice, decimal leverage)
    {
        return quantity == 0m
            ? 0m
            : ClampZero(Math.Abs(quantity) * entryPrice / leverage);
    }

    private static decimal CalculateMaintenanceMargin(decimal quantity, decimal markPrice, decimal maintenanceMarginRate)
    {
        return quantity == 0m
            ? 0m
            : ClampZero(Math.Abs(quantity) * markPrice * maintenanceMarginRate);
    }

    private static decimal CalculateCloseRealizedPnl(decimal quantity, decimal averageEntryPrice, decimal closePrice)
    {
        return quantity == 0m
            ? 0m
            : ClampZero((closePrice - averageEntryPrice) * quantity);
    }

    private static decimal? CalculateIsolatedLiquidationPrice(DemoPosition position)
    {
        if (position.Quantity == 0m ||
            position.MaintenanceMarginRate is null ||
            position.IsolatedMargin is null)
        {
            return null;
        }

        var absoluteQuantity = Math.Abs(position.Quantity);
        var maintenanceMarginRate = position.MaintenanceMarginRate.Value;
        decimal liquidationPrice;

        if (position.Quantity > 0m)
        {
            var denominator = absoluteQuantity * (1m - maintenanceMarginRate);

            if (denominator <= 0m)
            {
                return null;
            }

            liquidationPrice = ((position.AverageEntryPrice * absoluteQuantity) - position.IsolatedMargin.Value) / denominator;
        }
        else
        {
            var denominator = absoluteQuantity * (1m + maintenanceMarginRate);

            if (denominator <= 0m)
            {
                return null;
            }

            liquidationPrice = (position.IsolatedMargin.Value + (position.AverageEntryPrice * absoluteQuantity)) / denominator;
        }

        return liquidationPrice <= 0m
            ? null
            : ClampZero(liquidationPrice);
    }

    private static decimal? CalculateCrossLiquidationPrice(
        DemoPosition position,
        decimal availableCollateral,
        decimal otherUnrealizedPnl,
        decimal otherMaintenanceMargin)
    {
        if (position.Quantity == 0m || position.MaintenanceMarginRate is null)
        {
            return null;
        }

        var absoluteQuantity = Math.Abs(position.Quantity);
        var maintenanceMarginRate = position.MaintenanceMarginRate.Value;
        decimal liquidationPrice;

        if (position.Quantity > 0m)
        {
            var denominator = absoluteQuantity * (1m - maintenanceMarginRate);

            if (denominator <= 0m)
            {
                return null;
            }

            liquidationPrice =
                ((position.AverageEntryPrice * absoluteQuantity) - availableCollateral - otherUnrealizedPnl + otherMaintenanceMargin) /
                denominator;
        }
        else
        {
            var denominator = absoluteQuantity * (1m + maintenanceMarginRate);

            if (denominator <= 0m)
            {
                return null;
            }

            liquidationPrice =
                (availableCollateral + otherUnrealizedPnl + (position.AverageEntryPrice * absoluteQuantity) - otherMaintenanceMargin) /
                denominator;
        }

        return liquidationPrice <= 0m
            ? null
            : ClampZero(liquidationPrice);
    }

    private static bool ShouldLiquidate(DemoPosition position)
    {
        return position.Quantity != 0m &&
               position.MaintenanceMargin.HasValue &&
               position.MarginBalance.HasValue &&
               position.MarginBalance.Value <= position.MaintenanceMargin.Value + PrecisionEpsilon;
    }

    private static decimal ClampZero(decimal value)
    {
        return Math.Abs(value) <= PrecisionEpsilon
            ? 0m
            : value;
    }

    private sealed record FuturesFillOutcome(
        decimal NewQuantity,
        decimal NewAverageEntryPrice,
        decimal RealizedPnlDelta,
        bool ClosedExistingExposure);

    private sealed class WalletMutation(DemoWallet wallet)
    {
        public DemoWallet Wallet { get; } = wallet;

        public decimal AvailableDelta { get; private set; }

        public decimal ReservedDelta { get; private set; }

        public void Apply(decimal availableDelta, decimal reservedDelta)
        {
            var nextAvailable = ClampZero(Wallet.AvailableBalance + availableDelta);
            var nextReserved = ClampZero(Wallet.ReservedBalance + reservedDelta);

            if (nextAvailable < 0m)
            {
                throw new InvalidOperationException($"Insufficient available demo balance for asset '{Wallet.Asset}'.");
            }

            if (nextReserved < 0m)
            {
                throw new InvalidOperationException($"Insufficient reserved demo balance for asset '{Wallet.Asset}'.");
            }

            Wallet.AvailableBalance = nextAvailable;
            Wallet.ReservedBalance = nextReserved;
            AvailableDelta += availableDelta;
            ReservedDelta += reservedDelta;
        }
    }
}
