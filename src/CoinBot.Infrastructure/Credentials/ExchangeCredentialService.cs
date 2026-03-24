using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Credentials;

public sealed class ExchangeCredentialService(
    ApplicationDbContext dbContext,
    ICredentialCipher credentialCipher,
    IApiCredentialValidationService apiCredentialValidationService,
    IAuditLogService auditLogService,
    IOptions<CredentialSecurityOptions> options,
    TimeProvider timeProvider) : IExchangeCredentialService
{
    private readonly CredentialSecurityOptions optionsValue = options.Value;

    public async Task<ExchangeCredentialStateSnapshot> StoreAsync(
        StoreExchangeCredentialsRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeRequired(request.Actor, nameof(request.Actor));
        var apiKey = NormalizeSecret(request.ApiKey, nameof(request.ApiKey));
        var apiSecret = NormalizeSecret(request.ApiSecret, nameof(request.ApiSecret));
        var exchangeAccount = await GetTrackedExchangeAccountAsync(request.ExchangeAccountId, cancellationToken);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        exchangeAccount.ApiKeyCiphertext = credentialCipher.Encrypt(apiKey);
        exchangeAccount.ApiSecretCiphertext = credentialCipher.Encrypt(apiSecret);
        exchangeAccount.CredentialFingerprint = ComputeFingerprint(apiKey);
        exchangeAccount.CredentialKeyVersion = credentialCipher.KeyVersion;
        exchangeAccount.CredentialStatus = ExchangeCredentialStatus.PendingValidation;
        exchangeAccount.CredentialStoredAtUtc = utcNow;
        exchangeAccount.CredentialLastAccessedAtUtc = null;
        exchangeAccount.CredentialLastRotatedAtUtc = utcNow;
        exchangeAccount.CredentialRevalidateAfterUtc = null;
        exchangeAccount.CredentialRotateAfterUtc = utcNow.AddDays(optionsValue.RotationIntervalDays);
        exchangeAccount.LastValidatedAt = null;

        var state = CreateSnapshot(exchangeAccount, utcNow);

        await apiCredentialValidationService.UpsertStoredCredentialAsync(
            new ApiCredentialStoreMirrorRequest(
                exchangeAccount.Id,
                exchangeAccount.OwnerUserId,
                exchangeAccount.ApiKeyCiphertext!,
                exchangeAccount.ApiSecretCiphertext!,
                exchangeAccount.CredentialFingerprint!,
                exchangeAccount.CredentialKeyVersion!,
                credentialCipher.BlobVersion,
                utcNow),
            cancellationToken);

        await WriteAuditAsync(
            actor,
            "ExchangeCredential.Stored",
            request.ExchangeAccountId,
            state,
            outcome: "Applied",
            request.CorrelationId,
            purpose: null,
            accessMode: "EncryptedWrite",
            materials: "ApiKey,ApiSecret",
            cancellationToken);

        return state;
    }

    public async Task<ExchangeCredentialAccessResult> GetAsync(
        ExchangeCredentialAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeRequired(request.Actor, nameof(request.Actor));
        var exchangeAccount = await GetTrackedExchangeAccountAsync(request.ExchangeAccountId, cancellationToken);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var effectiveStatus = ResolveEffectiveStatus(exchangeAccount, utcNow);

        if (exchangeAccount.CredentialStatus != effectiveStatus)
        {
            exchangeAccount.CredentialStatus = effectiveStatus;
        }

        if (!CanAccess(request.Purpose, effectiveStatus))
        {
            var blockedState = CreateSnapshot(exchangeAccount, utcNow);

            await WriteAuditAsync(
                actor,
                "ExchangeCredential.Accessed",
                request.ExchangeAccountId,
                blockedState,
                MapBlockedOutcome(effectiveStatus),
                request.CorrelationId,
                request.Purpose,
                accessMode: "DecryptReadAttemptBlocked",
                materials: "ApiKey,ApiSecret",
                cancellationToken);

            throw new InvalidOperationException(CreateBlockedAccessMessage(request.Purpose, effectiveStatus));
        }

        string apiKey;
        string apiSecret;

        try
        {
            apiKey = credentialCipher.Decrypt(exchangeAccount.ApiKeyCiphertext!);
            apiSecret = credentialCipher.Decrypt(exchangeAccount.ApiSecretCiphertext!);
        }
        catch (InvalidOperationException)
        {
            exchangeAccount.CredentialStatus = ExchangeCredentialStatus.Invalid;
            var blockedState = CreateSnapshot(exchangeAccount, utcNow);

            await WriteAuditAsync(
                actor,
                "ExchangeCredential.Accessed",
                request.ExchangeAccountId,
                blockedState,
                outcome: "Blocked:DecryptFailed",
                request.CorrelationId,
                request.Purpose,
                accessMode: "DecryptReadFailed",
                materials: "ApiKey,ApiSecret",
                cancellationToken);

            throw new InvalidOperationException("Exchange credentials are unavailable because decryption failed.");
        }

        exchangeAccount.CredentialLastAccessedAtUtc = utcNow;
        var accessState = CreateSnapshot(exchangeAccount, utcNow);

        await WriteAuditAsync(
            actor,
            "ExchangeCredential.Accessed",
            request.ExchangeAccountId,
            accessState,
            outcome: "Allowed",
            request.CorrelationId,
            request.Purpose,
            accessMode: "DecryptRead",
            materials: "ApiKey,ApiSecret",
            cancellationToken);

        return new ExchangeCredentialAccessResult(apiKey, apiSecret, accessState);
    }

    public async Task<ExchangeCredentialStateSnapshot> SetValidationStateAsync(
        SetExchangeCredentialValidationStateRequest request,
        CancellationToken cancellationToken = default)
    {
        var actor = NormalizeRequired(request.Actor, nameof(request.Actor));
        var exchangeAccount = await GetTrackedExchangeAccountAsync(request.ExchangeAccountId, cancellationToken);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        if (!HasStoredCredentials(exchangeAccount))
        {
            throw new InvalidOperationException("Exchange credentials cannot be validated because no encrypted credential set exists.");
        }

        if (request.IsValid)
        {
            exchangeAccount.CredentialStatus = ExchangeCredentialStatus.Active;
            exchangeAccount.LastValidatedAt = utcNow;
            exchangeAccount.CredentialRevalidateAfterUtc = utcNow.AddDays(optionsValue.RevalidationIntervalDays);
            exchangeAccount.CredentialRotateAfterUtc ??= utcNow.AddDays(optionsValue.RotationIntervalDays);
            exchangeAccount.CredentialLastRotatedAtUtc ??= exchangeAccount.CredentialStoredAtUtc ?? utcNow;
        }
        else
        {
            exchangeAccount.CredentialStatus = ExchangeCredentialStatus.Invalid;
            exchangeAccount.LastValidatedAt = null;
            exchangeAccount.CredentialRevalidateAfterUtc = null;
        }

        var state = CreateSnapshot(exchangeAccount, utcNow);

        await apiCredentialValidationService.RecordValidationAsync(
            new ApiCredentialValidationRequest(
                exchangeAccount.Id,
                exchangeAccount.OwnerUserId,
                request.IsKeyValid,
                request.CanTrade,
                request.CanWithdraw,
                request.SupportsSpot,
                request.SupportsFutures,
                request.IsEnvironmentMatch,
                request.HasTimestampSkew,
                request.HasIpRestrictionIssue,
                request.EnvironmentScope,
                actor,
                request.CorrelationId,
                request.FailureReason,
                request.PermissionSummary,
                utcNow),
            cancellationToken);

        await WriteAuditAsync(
            actor,
            "ExchangeCredential.ValidationSet",
            request.ExchangeAccountId,
            state,
            outcome: request.IsValid ? "Validated" : "ValidationFailed",
            request.CorrelationId,
            purpose: ExchangeCredentialAccessPurpose.Validation,
            accessMode: "ValidationStateWrite",
            materials: "MetadataOnly",
            cancellationToken);

        return state;
    }

    public async Task<ExchangeCredentialStateSnapshot> GetStateAsync(
        Guid exchangeAccountId,
        CancellationToken cancellationToken = default)
    {
        var exchangeAccount = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == exchangeAccountId && !entity.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Exchange account '{exchangeAccountId}' was not found.");

        return CreateSnapshot(exchangeAccount, timeProvider.GetUtcNow().UtcDateTime);
    }

    private async Task<ExchangeAccount> GetTrackedExchangeAccountAsync(Guid exchangeAccountId, CancellationToken cancellationToken)
    {
        return await dbContext.ExchangeAccounts
            .SingleOrDefaultAsync(entity => entity.Id == exchangeAccountId && !entity.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Exchange account '{exchangeAccountId}' was not found.");
    }

    private static bool HasStoredCredentials(ExchangeAccount exchangeAccount)
    {
        return !string.IsNullOrWhiteSpace(exchangeAccount.ApiKeyCiphertext) &&
               !string.IsNullOrWhiteSpace(exchangeAccount.ApiSecretCiphertext);
    }

    private static bool CanAccess(ExchangeCredentialAccessPurpose purpose, ExchangeCredentialStatus status)
    {
        return purpose switch
        {
            ExchangeCredentialAccessPurpose.Execution => status == ExchangeCredentialStatus.Active,
            ExchangeCredentialAccessPurpose.Synchronization => status == ExchangeCredentialStatus.Active,
            ExchangeCredentialAccessPurpose.Validation => status != ExchangeCredentialStatus.Missing,
            _ => false
        };
    }

    private static ExchangeCredentialStatus ResolveEffectiveStatus(ExchangeAccount exchangeAccount, DateTime utcNow)
    {
        if (!HasStoredCredentials(exchangeAccount))
        {
            return ExchangeCredentialStatus.Missing;
        }

        if (exchangeAccount.CredentialStatus == ExchangeCredentialStatus.Active &&
            exchangeAccount.CredentialRotateAfterUtc.HasValue &&
            utcNow >= exchangeAccount.CredentialRotateAfterUtc.Value)
        {
            return ExchangeCredentialStatus.RotationRequired;
        }

        if (exchangeAccount.CredentialStatus == ExchangeCredentialStatus.Active &&
            exchangeAccount.CredentialRevalidateAfterUtc.HasValue &&
            utcNow >= exchangeAccount.CredentialRevalidateAfterUtc.Value)
        {
            return ExchangeCredentialStatus.RevalidationRequired;
        }

        return exchangeAccount.CredentialStatus;
    }

    private ExchangeCredentialStateSnapshot CreateSnapshot(ExchangeAccount exchangeAccount, DateTime utcNow)
    {
        var effectiveStatus = ResolveEffectiveStatus(exchangeAccount, utcNow);

        return new ExchangeCredentialStateSnapshot(
            exchangeAccount.Id,
            effectiveStatus,
            exchangeAccount.CredentialFingerprint,
            exchangeAccount.CredentialKeyVersion,
            exchangeAccount.CredentialStoredAtUtc,
            exchangeAccount.LastValidatedAt,
            exchangeAccount.CredentialLastAccessedAtUtc,
            exchangeAccount.CredentialLastRotatedAtUtc,
            exchangeAccount.CredentialRevalidateAfterUtc,
            exchangeAccount.CredentialRotateAfterUtc);
    }

    private async Task WriteAuditAsync(
        string actor,
        string action,
        Guid exchangeAccountId,
        ExchangeCredentialStateSnapshot state,
        string outcome,
        string? correlationId,
        ExchangeCredentialAccessPurpose? purpose,
        string accessMode,
        string materials,
        CancellationToken cancellationToken)
    {
        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                actor,
                action,
                $"ExchangeCredential/{exchangeAccountId}",
                BuildAuditContext(state, purpose, accessMode, materials),
                correlationId,
                outcome,
                "PrivatePlane"),
            cancellationToken);
    }

    private static string BuildAuditContext(
        ExchangeCredentialStateSnapshot state,
        ExchangeCredentialAccessPurpose? purpose,
        string accessMode,
        string materials)
    {
        var contextParts = new List<string>
        {
            $"AccessMode={accessMode}",
            $"Materials={materials}",
            $"Status={state.Status}",
            $"KeyVersion={state.KeyVersion ?? "missing"}",
            $"Fingerprint={ShortFingerprint(state.Fingerprint)}",
            $"StoredAtUtc={FormatTimestamp(state.StoredAtUtc)}",
            $"LastValidatedAtUtc={FormatTimestamp(state.LastValidatedAtUtc)}",
            $"LastAccessedAtUtc={FormatTimestamp(state.LastAccessedAtUtc)}",
            $"LastRotatedAtUtc={FormatTimestamp(state.LastRotatedAtUtc)}",
            $"RevalidateAfterUtc={FormatTimestamp(state.RevalidateAfterUtc)}",
            $"RotateAfterUtc={FormatTimestamp(state.RotateAfterUtc)}"
        };

        if (purpose is not null)
        {
            contextParts.Insert(0, $"Purpose={purpose.Value}");
        }

        var context = string.Join("; ", contextParts);
        return context.Length <= 2048
            ? context
            : context[..2048];
    }

    private static string CreateBlockedAccessMessage(
        ExchangeCredentialAccessPurpose purpose,
        ExchangeCredentialStatus status)
    {
        return purpose switch
        {
            ExchangeCredentialAccessPurpose.Execution =>
                CreateBlockedAccessMessage(status, "Execution"),
            ExchangeCredentialAccessPurpose.Synchronization =>
                CreateBlockedAccessMessage(status, "Synchronization"),
            _ => "Validation access blocked because no encrypted exchange credentials are stored."
        };
    }

    private static string CreateBlockedAccessMessage(
        ExchangeCredentialStatus status,
        string purposeLabel)
    {
        return status switch
        {
            ExchangeCredentialStatus.Missing => $"{purposeLabel} blocked because no encrypted exchange credentials are stored.",
            ExchangeCredentialStatus.PendingValidation => $"{purposeLabel} blocked because exchange credentials are pending validation.",
            ExchangeCredentialStatus.RevalidationRequired => $"{purposeLabel} blocked because exchange credentials must be re-validated.",
            ExchangeCredentialStatus.RotationRequired => $"{purposeLabel} blocked because exchange credentials must be rotated.",
            ExchangeCredentialStatus.Invalid => $"{purposeLabel} blocked because exchange credentials are marked invalid.",
            _ => $"{purposeLabel} blocked because exchange credentials are not available."
        };
    }

    private static string MapBlockedOutcome(ExchangeCredentialStatus status)
    {
        return status switch
        {
            ExchangeCredentialStatus.Missing => "Blocked:Missing",
            ExchangeCredentialStatus.PendingValidation => "Blocked:PendingValidation",
            ExchangeCredentialStatus.RevalidationRequired => "Blocked:RevalidationRequired",
            ExchangeCredentialStatus.RotationRequired => "Blocked:RotationRequired",
            ExchangeCredentialStatus.Invalid => "Blocked:Invalid",
            _ => "Blocked:Unavailable"
        };
    }

    private static string ComputeFingerprint(string apiKey)
    {
        var normalizedBytes = Encoding.UTF8.GetBytes(apiKey);

        try
        {
            return Convert.ToHexString(SHA256.HashData(normalizedBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(normalizedBytes);
        }
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }

    private static string NormalizeSecret(string? value, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }

    private static string FormatTimestamp(DateTime? value)
    {
        return value?.ToString("O") ?? "missing";
    }

    private static string ShortFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return "missing";
        }

        return fingerprint.Length <= 12
            ? fingerprint
            : fingerprint[..12];
    }
}
