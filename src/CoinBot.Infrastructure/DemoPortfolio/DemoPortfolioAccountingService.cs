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

        if (request.Side == DemoTradeSide.Buy)
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

        ApplyValuation(position, request.Price, request.MarkPrice ?? request.Price, occurredAtUtc);
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
        ApplyValuation(position, position.LastFillPrice, request.MarkPrice, occurredAtUtc);

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
            Side = request.Side,
            Quantity = request.Quantity,
            Price = request.Price,
            FeeAsset = feeAsset,
            FeeAmount = feeAmount == 0m ? null : feeAmount,
            FeeAmountInQuote = feeAmount == 0m ? null : feeAmountInQuote,
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
            entrySnapshots);

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
            transaction.MarkPriceAfter.HasValue ? transaction.OccurredAtUtc : null);
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
            position.LastValuationAtUtc);
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
                          entity.Quantity > 0m,
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
        transaction.PositionQuantityAfter = position.Quantity;
        transaction.PositionCostBasisAfter = position.CostBasis;
        transaction.PositionAverageEntryPriceAfter = position.AverageEntryPrice;
        transaction.CumulativeRealizedPnlAfter = position.RealizedPnl;
        transaction.UnrealizedPnlAfter = position.UnrealizedPnl;
        transaction.CumulativeFeesInQuoteAfter = position.TotalFeesInQuote;
        transaction.MarkPriceAfter = position.LastMarkPrice;
    }

    private static void EnsurePositionPair(DemoPosition position, string baseAsset, string quoteAsset)
    {
        if (!string.Equals(position.BaseAsset, baseAsset, StringComparison.Ordinal) ||
            !string.Equals(position.QuoteAsset, quoteAsset, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Demo position pair mismatch detected for the requested symbol.");
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

    private static decimal ClampZero(decimal value)
    {
        return Math.Abs(value) <= PrecisionEpsilon
            ? 0m
            : value;
    }

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
