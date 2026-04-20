using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Exchange;

public sealed class ExchangePositionSyncService(
    ApplicationDbContext dbContext,
    ILogger<ExchangePositionSyncService> logger)
{
    private static readonly ExecutionOrderState[] OpenOrderStates =
    [
        ExecutionOrderState.Received,
        ExecutionOrderState.GatePassed,
        ExecutionOrderState.Dispatching,
        ExecutionOrderState.Submitted,
        ExecutionOrderState.PartiallyFilled,
        ExecutionOrderState.CancelRequested
    ];

    public async Task ApplyAsync(ExchangeAccountSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var existingPositions = await dbContext.ExchangePositions
            .Where(entity =>
                entity.ExchangeAccountId == snapshot.ExchangeAccountId &&
                entity.Plane == snapshot.Plane)
            .ToListAsync(cancellationToken);
        var positionsByKey = existingPositions.ToDictionary(
            entity => CreateKey(entity.Symbol, entity.PositionSide),
            StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var positionSnapshot in snapshot.Positions)
        {
            var symbol = NormalizeCode(positionSnapshot.Symbol);
            var positionSide = NormalizeCode(positionSnapshot.PositionSide);
            var key = CreateKey(symbol, positionSide);
            seenKeys.Add(key);

            if (!positionsByKey.TryGetValue(key, out var entity))
            {
                entity = new ExchangePosition
                {
                    OwnerUserId = snapshot.OwnerUserId.Trim(),
                    ExchangeAccountId = snapshot.ExchangeAccountId,
                    Plane = snapshot.Plane,
                    Symbol = symbol,
                    PositionSide = positionSide
                };
                dbContext.ExchangePositions.Add(entity);
                positionsByKey[key] = entity;
            }

            entity.OwnerUserId = snapshot.OwnerUserId.Trim();
            entity.Plane = snapshot.Plane;
            entity.Symbol = symbol;
            entity.PositionSide = positionSide;
            entity.IsDeleted = false;
            entity.Quantity = positionSnapshot.Quantity;
            entity.EntryPrice = positionSnapshot.EntryPrice;
            entity.BreakEvenPrice = positionSnapshot.BreakEvenPrice;
            entity.UnrealizedProfit = positionSnapshot.UnrealizedProfit;
            entity.MarginType = NormalizeMarginType(positionSnapshot.MarginType);
            entity.IsolatedWallet = positionSnapshot.IsolatedWallet;
            entity.ExchangeUpdatedAtUtc = NormalizeTimestamp(positionSnapshot.ExchangeUpdatedAtUtc);
            entity.SyncedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc);
        }

        foreach (var existingPosition in existingPositions)
        {
            if (seenKeys.Contains(CreateKey(existingPosition.Symbol, existingPosition.PositionSide)))
            {
                continue;
            }

            existingPosition.IsDeleted = true;
            existingPosition.Quantity = 0m;
            existingPosition.EntryPrice = 0m;
            existingPosition.BreakEvenPrice = 0m;
            existingPosition.UnrealizedProfit = 0m;
            existingPosition.IsolatedWallet = 0m;
            existingPosition.SyncedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await RefreshLinkedBotCountersAsync(snapshot, cancellationToken);

        logger.LogDebug(
            "Exchange positions synchronized for account {ExchangeAccountId}. Plane={Plane}. Count={PositionCount}.",
            snapshot.ExchangeAccountId,
            snapshot.Plane,
            snapshot.Positions.Count);
    }

    private async Task RefreshLinkedBotCountersAsync(ExchangeAccountSnapshot snapshot, CancellationToken cancellationToken)
    {
        var ownerUserId = snapshot.OwnerUserId.Trim();
        var bots = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.ExchangeAccountId == snapshot.ExchangeAccountId &&
                !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        if (bots.Count == 0)
        {
            return;
        }

        foreach (var bot in bots)
        {
            bot.OpenOrderCount = await dbContext.ExecutionOrders
                .IgnoreQueryFilters()
                .CountAsync(
                    entity => entity.BotId == bot.Id &&
                              !entity.IsDeleted &&
                              OpenOrderStates.Contains(entity.State),
                    cancellationToken);
            bot.OpenPositionCount = await ResolveOpenPositionCountFromLatestSnapshotAsync(
                snapshot,
                bot.Symbol,
                cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> ResolveOpenPositionCountFromLatestSnapshotAsync(
        ExchangeAccountSnapshot snapshot,
        string? botSymbol,
        CancellationToken cancellationToken)
    {
        var ownerUserId = snapshot.OwnerUserId.Trim();
        var normalizedSymbol = string.IsNullOrWhiteSpace(botSymbol) ? null : NormalizeCode(botSymbol);
        var syncCutoffUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc);
        var projectedPositions = new Dictionary<string, decimal>(StringComparer.Ordinal);

        var activePositions = (await dbContext.ExchangePositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.ExchangeAccountId == snapshot.ExchangeAccountId &&
                    entity.Plane == snapshot.Plane &&
                    !entity.IsDeleted &&
                    entity.Quantity != 0m)
                .ToListAsync(cancellationToken))
            .Where(entity => normalizedSymbol is null || NormalizeCode(entity.Symbol) == normalizedSymbol)
            .ToList();

        foreach (var position in activePositions)
        {
            var symbol = NormalizeCode(position.Symbol);
            projectedPositions[symbol] = projectedPositions.GetValueOrDefault(symbol) +
                ResolveSignedPositionQuantity(position.Quantity, position.PositionSide);
        }

        var liveOrdersAfterSnapshot = (await dbContext.ExecutionOrders
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.ExchangeAccountId == snapshot.ExchangeAccountId &&
                    entity.Plane == snapshot.Plane &&
                    !entity.IsDeleted &&
                    entity.SubmittedToBroker &&
                    (entity.State == ExecutionOrderState.Submitted ||
                     entity.State == ExecutionOrderState.Dispatching ||
                     entity.State == ExecutionOrderState.CancelRequested ||
                     entity.State == ExecutionOrderState.PartiallyFilled ||
                     entity.State == ExecutionOrderState.Filled))
                .ToListAsync(cancellationToken))
            .Where(entity => normalizedSymbol is null || NormalizeCode(entity.Symbol) == normalizedSymbol)
            .ToList();

        foreach (var order in liveOrdersAfterSnapshot)
        {
            if (ResolveOrderEffectiveAtUtc(order) <= syncCutoffUtc)
            {
                continue;
            }

            var quantity = ResolveSignedOrderQuantity(order);
            if (quantity == 0m)
            {
                continue;
            }

            var symbol = NormalizeCode(order.Symbol);
            projectedPositions[symbol] = projectedPositions.GetValueOrDefault(symbol) + quantity;
        }

        return projectedPositions.Values.Count(quantity => quantity != 0m);
    }

    private static decimal ResolveSignedPositionQuantity(decimal quantity, string? positionSide)
    {
        if (quantity == 0m)
        {
            return 0m;
        }

        return NormalizeCode(positionSide ?? "BOTH") == "SHORT"
            ? -Math.Abs(quantity)
            : quantity;
    }

    private static decimal ResolveSignedOrderQuantity(ExecutionOrder order)
    {
        var quantity = order.FilledQuantity;
        if (quantity == 0m)
        {
            return 0m;
        }

        return order.Side == ExecutionOrderSide.Buy ? quantity : -quantity;
    }

    private static DateTime ResolveOrderEffectiveAtUtc(ExecutionOrder order)
    {
        return NormalizeTimestamp(
            order.LastFilledAtUtc ??
            order.SubmittedAtUtc ??
            (order.LastStateChangedAtUtc != default ? order.LastStateChangedAtUtc : (DateTime?)null) ??
            (order.UpdatedDate != default ? order.UpdatedDate : (DateTime?)null) ??
            order.CreatedDate);
    }

    private static string CreateKey(string symbol, string positionSide)
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
