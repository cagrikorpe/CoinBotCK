using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Exchange;

internal static class ExchangeSyncAccountSelection
{
    public static async Task<IReadOnlyCollection<ExchangeSyncAccountDescriptor>> ListAsync(
        ApplicationDbContext dbContext,
        ExchangeDataPlane plane,
        CancellationToken cancellationToken = default)
    {
        var accounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.ExchangeName == "Binance" &&
                entity.ApiKeyCiphertext != null &&
                entity.ApiSecretCiphertext != null)
            .Select(entity => new
            {
                entity.Id,
                entity.OwnerUserId,
                entity.ExchangeName
            })
            .ToListAsync(cancellationToken);

        if (accounts.Count == 0)
        {
            return [];
        }

        var accountIds = accounts.Select(entity => entity.Id).ToArray();
        var validations = await dbContext.ApiCredentialValidations
            .AsNoTracking()
            .Where(entity =>
                accountIds.Contains(entity.ExchangeAccountId) &&
                !entity.IsDeleted)
            .OrderByDescending(entity => entity.ValidatedAtUtc)
            .ToListAsync(cancellationToken);
        var latestValidationLookup = validations
            .GroupBy(entity => entity.ExchangeAccountId)
            .ToDictionary(group => group.Key, group => group.First());

        return accounts
            .Where(account => SupportsPlane(
                latestValidationLookup.GetValueOrDefault(account.Id),
                plane))
            .Select(account => new ExchangeSyncAccountDescriptor(
                account.Id,
                account.OwnerUserId,
                account.ExchangeName,
                plane))
            .ToArray();
    }

    private static bool SupportsPlane(ApiCredentialValidation? validation, ExchangeDataPlane plane)
    {
        if (validation is null)
        {
            return plane == ExchangeDataPlane.Futures;
        }

        return plane == ExchangeDataPlane.Spot
            ? validation.SupportsSpot
            : validation.SupportsFutures;
    }
}
