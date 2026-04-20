using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Execution;

internal static class LivePositionTruthResolver
{
    internal sealed record ProjectedPositionRow(
        Guid? ExchangeAccountId,
        string Symbol,
        decimal NetQuantity,
        decimal ReferencePrice,
        DateTime? SyncCutoffUtc);

    public static async Task<int> ResolveOpenPositionCountAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        ExchangeDataPlane plane,
        Guid? exchangeAccountId,
        CancellationToken cancellationToken)
    {
        var rows = await ResolveProjectedPositionsAsync(
            dbContext,
            ownerUserId,
            plane,
            exchangeAccountId,
            cancellationToken);

        return rows.Count(row => row.NetQuantity != 0m);
    }

    public static async Task<decimal> ResolveNetQuantityAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        ExchangeDataPlane plane,
        Guid? exchangeAccountId,
        string symbol,
        CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        if (normalizedSymbol.Length == 0)
        {
            return 0m;
        }

        var rows = await ResolveProjectedPositionsAsync(
            dbContext,
            ownerUserId,
            plane,
            exchangeAccountId,
            cancellationToken);

        return rows
            .Where(row => string.Equals(row.Symbol, normalizedSymbol, StringComparison.Ordinal))
            .Sum(row => row.NetQuantity);
    }

    public static async Task<IReadOnlyCollection<ProjectedPositionRow>> ResolveProjectedPositionsAsync(
        ApplicationDbContext dbContext,
        string ownerUserId,
        ExchangeDataPlane plane,
        Guid? exchangeAccountId,
        CancellationToken cancellationToken)
    {
        var normalizedOwnerUserId = ownerUserId.Trim();
        var accountSyncCutoffs = (await dbContext.ExchangeAccountSyncStates
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == normalizedOwnerUserId &&
                    entity.Plane == plane &&
                    !entity.IsDeleted &&
                    (!exchangeAccountId.HasValue || entity.ExchangeAccountId == exchangeAccountId.Value))
                .ToListAsync(cancellationToken))
            .GroupBy(entity => entity.ExchangeAccountId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entity => entity.LastPositionSyncedAtUtc ?? entity.LastStateReconciledAtUtc)
                    .Where(entity => entity.HasValue)
                    .Select(entity => NormalizeUtc(entity.Value))
                    .Cast<DateTime?>()
                    .DefaultIfEmpty(null)
                    .Max());

        var activePositions = (await dbContext.ExchangePositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == normalizedOwnerUserId &&
                    entity.Plane == plane &&
                    !entity.IsDeleted &&
                    entity.Quantity != 0m &&
                    (!exchangeAccountId.HasValue || entity.ExchangeAccountId == exchangeAccountId.Value))
                .ToListAsync(cancellationToken))
            .Select(entity => new RawPosition(
                entity.ExchangeAccountId,
                NormalizeSymbol(entity.Symbol),
                entity.PositionSide,
                entity.Quantity,
                entity.EntryPrice,
                entity.BreakEvenPrice,
                entity.SyncedAtUtc,
                entity.UpdatedDate,
                entity.CreatedDate))
            .ToList();

        var positionRowsByKey = activePositions
            .GroupBy(entity => new PositionKey(entity.ExchangeAccountId, entity.Symbol))
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var latestPosition = group
                        .OrderByDescending(entity => NormalizeUtc(entity.SyncedAtUtc))
                        .ThenByDescending(entity => NormalizeUtc(entity.UpdatedDate))
                        .ThenByDescending(entity => NormalizeUtc(entity.CreatedDate))
                        .First();
                    var accountCutoffUtc = accountSyncCutoffs.GetValueOrDefault(group.Key.ExchangeAccountId);
                    var positionCutoffUtc = group.Max(entity => NormalizeUtc(entity.SyncedAtUtc));
                    var syncCutoffUtc = MaxUtc(accountCutoffUtc, positionCutoffUtc);
                    return new ProjectedPositionAccumulator(
                        group.Key.ExchangeAccountId,
                        group.Key.Symbol,
                        group.Sum(entity => ResolveSignedPositionQuantity(entity.Quantity, entity.PositionSide)),
                        latestPosition.EntryPrice != 0m ? latestPosition.EntryPrice : latestPosition.BreakEvenPrice,
                        syncCutoffUtc);
                });

        var executionOrders = (await dbContext.ExecutionOrders
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == normalizedOwnerUserId &&
                    entity.Plane == plane &&
                    !entity.IsDeleted &&
                    entity.SubmittedToBroker &&
                    (!exchangeAccountId.HasValue || entity.ExchangeAccountId == exchangeAccountId.Value) &&
                    (entity.State == ExecutionOrderState.Submitted ||
                     entity.State == ExecutionOrderState.Dispatching ||
                     entity.State == ExecutionOrderState.CancelRequested ||
                     entity.State == ExecutionOrderState.PartiallyFilled ||
                     entity.State == ExecutionOrderState.Filled))
                .ToListAsync(cancellationToken))
            .Select(entity => new RawOrder(
                entity.ExchangeAccountId,
                NormalizeSymbol(entity.Symbol),
                entity.Side,
                entity.Quantity,
                entity.FilledQuantity,
                entity.ReduceOnly,
                entity.OrderType,
                entity.State,
                entity.Price,
                entity.AverageFillPrice,
                entity.SubmittedAtUtc,
                entity.LastFilledAtUtc,
                entity.LastStateChangedAtUtc,
                entity.UpdatedDate,
                entity.CreatedDate))
            .ToList();

        foreach (var orderGroup in executionOrders
                     .Where(entity => entity.ExchangeAccountId.HasValue && entity.Symbol.Length != 0)
                     .GroupBy(entity => new PositionKey(entity.ExchangeAccountId!.Value, entity.Symbol)))
        {
            var key = orderGroup.Key;
            var accountCutoffUtc = accountSyncCutoffs.GetValueOrDefault(key.ExchangeAccountId);
            if (!positionRowsByKey.TryGetValue(key, out var accumulator))
            {
                accumulator = new ProjectedPositionAccumulator(
                    key.ExchangeAccountId,
                    key.Symbol,
                    0m,
                    0m,
                    accountCutoffUtc);
            }

            var filteredOrders = orderGroup
                .Where(entity =>
                {
                    var effectiveAtUtc = ResolveOrderEffectiveAtUtc(
                        entity.LastStateChangedAtUtc,
                        entity.LastFilledAtUtc,
                        entity.SubmittedAtUtc,
                        entity.UpdatedDate,
                        entity.CreatedDate);
                    return !accumulator.SyncCutoffUtc.HasValue || effectiveAtUtc > accumulator.SyncCutoffUtc.Value;
                })
                .OrderBy(entity => ResolveOrderEffectiveAtUtc(
                    entity.LastStateChangedAtUtc,
                    entity.LastFilledAtUtc,
                    entity.SubmittedAtUtc,
                    entity.UpdatedDate,
                    entity.CreatedDate))
                .ToList();

            if (filteredOrders.Count == 0)
            {
                positionRowsByKey[key] = accumulator;
                continue;
            }

            var deltaQuantity = filteredOrders.Sum(ResolveSignedOrderQuantity);
            var latestOrder = filteredOrders[^1];
            var referencePrice = latestOrder.AverageFillPrice.GetValueOrDefault() != 0m
                ? latestOrder.AverageFillPrice.GetValueOrDefault()
                : latestOrder.Price;

            positionRowsByKey[key] = accumulator with
            {
                NetQuantity = accumulator.NetQuantity + deltaQuantity,
                ReferencePrice = referencePrice != 0m ? referencePrice : accumulator.ReferencePrice
            };
        }

        return positionRowsByKey.Values
            .Where(entity => entity.NetQuantity != 0m)
            .OrderBy(entity => entity.ExchangeAccountId)
            .ThenBy(entity => entity.Symbol, StringComparer.Ordinal)
            .Select(entity => new ProjectedPositionRow(
                entity.ExchangeAccountId,
                entity.Symbol,
                entity.NetQuantity,
                entity.ReferencePrice,
                entity.SyncCutoffUtc))
            .ToArray();
    }

    private static decimal ResolveSignedOrderQuantity(RawOrder entity)
    {
        var quantity = (decimal)entity.FilledQuantity;
        if (quantity == 0m)
        {
            return 0m;
        }

        return entity.Side == ExecutionOrderSide.Buy
            ? quantity
            : -quantity;
    }

    private static decimal ResolveSignedPositionQuantity(decimal quantity, string? positionSide)
    {
        if (quantity == 0m)
        {
            return 0m;
        }

        return NormalizePositionSide(positionSide) == "SHORT"
            ? -Math.Abs(quantity)
            : quantity;
    }

    private static string NormalizePositionSide(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "BOTH"
            : value.Trim().ToUpperInvariant();
    }

    private static string NormalizeSymbol(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static DateTime ResolveOrderEffectiveAtUtc(
        DateTime lastStateChangedAtUtc,
        DateTime? lastFilledAtUtc,
        DateTime? submittedAtUtc,
        DateTime updatedDate,
        DateTime createdDate)
    {
        var candidate = lastFilledAtUtc
            ?? submittedAtUtc
            ?? (lastStateChangedAtUtc != default ? lastStateChangedAtUtc : (DateTime?)null)
            ?? (updatedDate != default ? updatedDate : (DateTime?)null)
            ?? createdDate;
        return NormalizeUtc(candidate);
    }

    private static DateTime? MaxUtc(DateTime? left, DateTime? right)
    {
        if (!left.HasValue)
        {
            return right;
        }

        if (!right.HasValue)
        {
            return left;
        }

        return NormalizeUtc(left.Value) >= NormalizeUtc(right.Value)
            ? NormalizeUtc(left.Value)
            : NormalizeUtc(right.Value);
    }

    private readonly record struct PositionKey(Guid ExchangeAccountId, string Symbol);

    private readonly record struct RawPosition(
        Guid ExchangeAccountId,
        string Symbol,
        string PositionSide,
        decimal Quantity,
        decimal EntryPrice,
        decimal BreakEvenPrice,
        DateTime SyncedAtUtc,
        DateTime UpdatedDate,
        DateTime CreatedDate);

    private readonly record struct RawOrder(
        Guid? ExchangeAccountId,
        string Symbol,
        ExecutionOrderSide Side,
        decimal Quantity,
        decimal FilledQuantity,
        bool ReduceOnly,
        ExecutionOrderType OrderType,
        ExecutionOrderState State,
        decimal Price,
        decimal? AverageFillPrice,
        DateTime? SubmittedAtUtc,
        DateTime? LastFilledAtUtc,
        DateTime LastStateChangedAtUtc,
        DateTime UpdatedDate,
        DateTime CreatedDate);

    private readonly record struct ProjectedPositionAccumulator(
        Guid ExchangeAccountId,
        string Symbol,
        decimal NetQuantity,
        decimal ReferencePrice,
        DateTime? SyncCutoffUtc);
}
