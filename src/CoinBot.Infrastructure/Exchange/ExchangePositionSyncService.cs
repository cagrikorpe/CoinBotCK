using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Exchange;

public sealed class ExchangePositionSyncService(
    ApplicationDbContext dbContext,
    ILogger<ExchangePositionSyncService> logger)
{
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
            existingPosition.SyncedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Exchange positions synchronized for account {ExchangeAccountId}. Plane={Plane}. Count={PositionCount}.",
            snapshot.ExchangeAccountId,
            snapshot.Plane,
            snapshot.Positions.Count);
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
