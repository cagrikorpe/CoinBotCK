using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Administration;

public sealed class ApiCredentialValidationService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IApiCredentialValidationService
{
    public async Task UpsertStoredCredentialAsync(
        ApiCredentialStoreMirrorRequest request,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.ApiCredentials
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                item => item.ExchangeAccountId == request.ExchangeAccountId &&
                        !item.IsDeleted,
                cancellationToken);

        if (entity is null)
        {
            entity = new ApiCredential
            {
                ExchangeAccountId = request.ExchangeAccountId
            };

            dbContext.ApiCredentials.Add(entity);
        }

        entity.OwnerUserId = NormalizeRequired(request.OwnerUserId, 450, nameof(request.OwnerUserId));
        entity.ApiKeyCiphertext = NormalizeRequired(request.ApiKeyCiphertext, 4096, nameof(request.ApiKeyCiphertext));
        entity.ApiSecretCiphertext = NormalizeRequired(request.ApiSecretCiphertext, 4096, nameof(request.ApiSecretCiphertext));
        entity.CredentialFingerprint = NormalizeRequired(request.CredentialFingerprint, 128, nameof(request.CredentialFingerprint));
        entity.KeyVersion = NormalizeRequired(request.KeyVersion, 64, nameof(request.KeyVersion));
        entity.EncryptedBlobVersion = request.EncryptedBlobVersion;
        entity.ValidationStatus = "Pending";
        entity.PermissionSummary = null;
        entity.StoredAtUtc = request.StoredAtUtc.ToUniversalTime();
        entity.LastValidatedAtUtc = null;
        entity.LastFailureReason = null;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ApiCredentialValidationSnapshot> RecordValidationAsync(
        ApiCredentialValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var credential = await dbContext.ApiCredentials
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.ExchangeAccountId == request.ExchangeAccountId &&
                          !entity.IsDeleted,
                cancellationToken)
            ?? throw new InvalidOperationException($"API credential mirror for exchange account '{request.ExchangeAccountId}' was not found.");

        var validatedAtUtc = request.ValidatedAtUtc?.ToUniversalTime() ?? timeProvider.GetUtcNow().UtcDateTime;
        var validationStatus = ResolveValidationStatus(request);
        var permissionSummary = NormalizeOptional(
            request.PermissionSummary,
            256) ?? BuildPermissionSummary(request);
        var failureReason = NormalizeOptional(
            request.FailureReason,
            512) ?? ResolveFailureReason(request);
        var validation = new ApiCredentialValidation
        {
            ApiCredentialId = credential.Id,
            ExchangeAccountId = credential.ExchangeAccountId,
            OwnerUserId = NormalizeRequired(request.OwnerUserId, 450, nameof(request.OwnerUserId)),
            IsKeyValid = request.IsKeyValid,
            CanTrade = request.CanTrade,
            CanWithdraw = request.CanWithdraw,
            SupportsSpot = request.SupportsSpot,
            SupportsFutures = request.SupportsFutures,
            EnvironmentScope = NormalizeOptional(request.EnvironmentScope, 32),
            IsEnvironmentMatch = request.IsEnvironmentMatch,
            HasTimestampSkew = request.HasTimestampSkew,
            HasIpRestrictionIssue = request.HasIpRestrictionIssue,
            ValidationStatus = validationStatus,
            PermissionSummary = permissionSummary,
            FailureReason = failureReason,
            CorrelationId = NormalizeOptional(request.CorrelationId, 128),
            ValidatedAtUtc = validatedAtUtc
        };

        dbContext.ApiCredentialValidations.Add(validation);
        credential.ValidationStatus = validationStatus;
        credential.PermissionSummary = permissionSummary;
        credential.LastValidatedAtUtc = validatedAtUtc;
        credential.LastFailureReason = failureReason;
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ApiCredentialValidationSnapshot(
            credential.Id,
            credential.ExchangeAccountId,
            credential.OwnerUserId,
            validationStatus,
            permissionSummary,
            failureReason,
            request.IsKeyValid,
            request.CanTrade,
            request.CanWithdraw,
            request.SupportsSpot,
            request.SupportsFutures,
            request.IsEnvironmentMatch,
            request.HasTimestampSkew,
            request.HasIpRestrictionIssue,
            validation.EnvironmentScope,
            validatedAtUtc);
    }

    public async Task<IReadOnlyCollection<ApiCredentialAdminSummary>> ListAdminSummariesAsync(
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var normalizedTake = take is > 0 and <= 500
            ? take
            : 100;
        var accounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .Take(normalizedTake)
            .Select(entity => new
            {
                entity.Id,
                entity.OwnerUserId,
                entity.ExchangeName,
                entity.DisplayName,
                entity.IsReadOnly
            })
            .ToListAsync(cancellationToken);
        var exchangeAccountIds = accounts.Select(entity => entity.Id).ToArray();
        var credentials = await dbContext.ApiCredentials
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => exchangeAccountIds.Contains(entity.ExchangeAccountId) && !entity.IsDeleted)
            .ToDictionaryAsync(entity => entity.ExchangeAccountId, cancellationToken);

        return accounts
            .Select(account =>
            {
                credentials.TryGetValue(account.Id, out var credential);

                return new ApiCredentialAdminSummary(
                    account.Id,
                    account.OwnerUserId,
                    account.ExchangeName,
                    account.DisplayName,
                    account.IsReadOnly,
                    SensitivePayloadMasker.MaskFingerprint(credential?.CredentialFingerprint),
                    credential?.ValidationStatus ?? "Missing",
                    credential?.PermissionSummary,
                    credential?.LastValidatedAtUtc,
                    credential?.LastFailureReason);
            })
            .ToArray();
    }

    private static string ResolveValidationStatus(ApiCredentialValidationRequest request)
    {
        return request.IsKeyValid &&
               request.CanTrade &&
               !request.CanWithdraw &&
               request.IsEnvironmentMatch &&
               !request.HasTimestampSkew &&
               !request.HasIpRestrictionIssue
            ? "Valid"
            : "Invalid";
    }

    private static string BuildPermissionSummary(ApiCredentialValidationRequest request)
    {
        return
            $"Trade={(request.CanTrade ? "Y" : "N")}; Withdraw={(request.CanWithdraw ? "Y" : "N")}; Spot={(request.SupportsSpot ? "Y" : "N")}; Futures={(request.SupportsFutures ? "Y" : "N")}; Env={(request.EnvironmentScope ?? "Unknown")}";
    }

    private static string? ResolveFailureReason(ApiCredentialValidationRequest request)
    {
        if (!request.IsKeyValid)
        {
            return "API key rejected.";
        }

        if (!request.CanTrade)
        {
            return "Trade permission missing.";
        }

        if (request.CanWithdraw)
        {
            return "Withdraw permission must stay disabled.";
        }

        if (!request.IsEnvironmentMatch)
        {
            return "Credential environment mismatch detected.";
        }

        if (request.HasTimestampSkew)
        {
            return "Exchange timestamp or recvWindow validation failed.";
        }

        if (request.HasIpRestrictionIssue)
        {
            return "Credential IP restriction prevented validation.";
        }

        return null;
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
    {
        var normalizedValue = NormalizeOptional(value, maxLength);

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }
}
