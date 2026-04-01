using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace CoinBot.Infrastructure.Exchange;

public sealed class ExchangeBalanceSyncService(
    ApplicationDbContext dbContext,
    ILogger<ExchangeBalanceSyncService> logger)
{
    public async Task ApplyAsync(ExchangeAccountSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var existingBalances = await dbContext.ExchangeBalances
            .Where(entity => entity.ExchangeAccountId == snapshot.ExchangeAccountId)
            .ToListAsync(cancellationToken);
        var balancesByAsset = existingBalances.ToDictionary(
            entity => NormalizeCode(entity.Asset),
            StringComparer.Ordinal);
        var seenAssets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var balanceSnapshot in snapshot.Balances)
        {
            var asset = NormalizeCode(balanceSnapshot.Asset);
            seenAssets.Add(asset);

            if (!balancesByAsset.TryGetValue(asset, out var entity))
            {
                entity = new ExchangeBalance
                {
                    OwnerUserId = snapshot.OwnerUserId.Trim(),
                    ExchangeAccountId = snapshot.ExchangeAccountId,
                    Asset = asset
                };
                dbContext.ExchangeBalances.Add(entity);
                balancesByAsset[asset] = entity;
            }

            entity.Asset = asset;
            entity.OwnerUserId = snapshot.OwnerUserId.Trim();
            entity.IsDeleted = false;
            entity.WalletBalance = balanceSnapshot.WalletBalance;
            entity.CrossWalletBalance = balanceSnapshot.CrossWalletBalance;
            entity.AvailableBalance = balanceSnapshot.AvailableBalance;
            entity.MaxWithdrawAmount = balanceSnapshot.MaxWithdrawAmount;
            entity.ExchangeUpdatedAtUtc = NormalizeTimestamp(balanceSnapshot.ExchangeUpdatedAtUtc);
            entity.SyncedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc);
        }

        foreach (var existingBalance in existingBalances)
        {
            if (seenAssets.Contains(NormalizeCode(existingBalance.Asset)))
            {
                continue;
            }

            existingBalance.IsDeleted = true;
            existingBalance.SyncedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Exchange balances synchronized for account {ExchangeAccountId}. OwnerKey={OwnerKey}. Count={BalanceCount}.",
            snapshot.ExchangeAccountId,
            CreateOwnerKey(snapshot.OwnerUserId),
            snapshot.Balances.Count);
    }

    private static string NormalizeCode(string value)
    {
        return value.Trim().ToUpperInvariant();
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

    private static string CreateOwnerKey(string ownerUserId)
    {
        var normalizedValue = ownerUserId.Trim();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedValue));
        return Convert.ToHexString(hashBytes[..6]);
    }
}
