using System.Security.Cryptography;
using System.Text;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.Mfa;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Mfa;

public sealed class MfaManagementService(
    ApplicationDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    ITotpService totpService,
    IEmailOtpService emailOtpService,
    IPasswordHasher<ApplicationUser> passwordHasher,
    IAuditLogService auditLogService,
    TimeProvider timeProvider) : IMfaManagementService
{
    private const int RecoveryCodeCount = 8;
    private const string AuditEnvironment = "Identity";

    public async Task<MfaStatusSnapshot> GetStatusAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await ResolveUserAsync(userId, cancellationToken);
        var activeRecoveryCodeCount = await dbContext.MfaRecoveryCodes.CountAsync(
            entity => entity.UserId == user.Id &&
                      entity.ConsumedAtUtc == null &&
                      !entity.IsDeleted,
            cancellationToken);

        return MapStatus(user, activeRecoveryCodeCount);
    }

    public async Task<MfaAuthenticatorSetupSnapshot?> GetAuthenticatorSetupAsync(
        string userId,
        bool createIfMissing = false,
        CancellationToken cancellationToken = default)
    {
        var user = await ResolveUserAsync(userId, cancellationToken);

        if (user.TotpEnabled)
        {
            return null;
        }

        var authenticatorKey = await userManager.GetAuthenticatorKeyAsync(user);
        var hasPendingSecret = !string.IsNullOrWhiteSpace(user.TotpSecretCiphertext);

        if (!hasPendingSecret && !createIfMissing)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(authenticatorKey) || !hasPendingSecret)
        {
            authenticatorKey = await ResetAuthenticatorSetupAsync(user, cancellationToken);

            await auditLogService.WriteAsync(
                new AuditLogWriteRequest(
                    user.Id,
                    "Identity.MfaSetupStarted",
                    $"User/{user.Id}/Mfa",
                    "provider=authenticator-app",
                    CorrelationId: null,
                    Outcome: "Applied",
                    Environment: AuditEnvironment),
                cancellationToken);
        }

        return new MfaAuthenticatorSetupSnapshot(
            authenticatorKey,
            FormatSharedKey(authenticatorKey),
            BuildAuthenticatorUri(user, authenticatorKey));
    }

    public async Task<IReadOnlyList<string>?> EnableAuthenticatorAsync(
        string userId,
        string code,
        CancellationToken cancellationToken = default)
    {
        var user = await ResolveUserAsync(userId, cancellationToken);

        if (string.IsNullOrWhiteSpace(user.TotpSecretCiphertext))
        {
            return null;
        }

        if (!totpService.VerifyCode(user.TotpSecretCiphertext, code))
        {
            return null;
        }

        user.TotpEnabled = true;
        user.EmailOtpEnabled = false;
        user.MfaEnabled = true;
        user.TwoFactorEnabled = true;
        user.PreferredMfaProvider = MfaProviders.AuthenticatorApp;
        user.MfaUpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        await UpdateUserAsync(user, cancellationToken);
        var recoveryCodes = await ReplaceRecoveryCodesAsync(user, cancellationToken);

        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                user.Id,
                "Identity.MfaEnabled",
                $"User/{user.Id}/Mfa",
                $"provider={MfaProviders.AuthenticatorApp};recoveryCodes={recoveryCodes.Count}",
                CorrelationId: null,
                Outcome: "Applied",
                Environment: AuditEnvironment),
            cancellationToken);

        return recoveryCodes;
    }

    public async Task<bool> DisableAsync(
        string userId,
        string verificationCode,
        CancellationToken cancellationToken = default)
    {
        var user = await ResolveUserAsync(userId, cancellationToken);

        if (!user.MfaEnabled)
        {
            return true;
        }

        if (!await ValidateManagementCodeAsync(user, verificationCode, cancellationToken))
        {
            return false;
        }

        user.TotpEnabled = false;
        user.EmailOtpEnabled = false;
        user.MfaEnabled = false;
        user.TwoFactorEnabled = false;
        user.PreferredMfaProvider = null;
        user.TotpSecretCiphertext = null;
        user.MfaUpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        await UpdateUserAsync(user, cancellationToken);
        await RemoveRecoveryCodesAsync(user.Id, cancellationToken);

        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                user.Id,
                "Identity.MfaDisabled",
                $"User/{user.Id}/Mfa",
                "provider=authenticator-app",
                CorrelationId: null,
                Outcome: "Applied",
                Environment: AuditEnvironment),
            cancellationToken);

        return true;
    }

    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(
        string userId,
        string verificationCode,
        CancellationToken cancellationToken = default)
    {
        var user = await ResolveUserAsync(userId, cancellationToken);

        if (!user.MfaEnabled || !user.TotpEnabled)
        {
            return null;
        }

        if (!await ValidateManagementCodeAsync(user, verificationCode, cancellationToken))
        {
            return null;
        }

        var recoveryCodes = await ReplaceRecoveryCodesAsync(user, cancellationToken);
        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                user.Id,
                "Identity.MfaRecoveryCodesRegenerated",
                $"User/{user.Id}/Mfa",
                $"recoveryCodes={recoveryCodes.Count}",
                CorrelationId: null,
                Outcome: "Applied",
                Environment: AuditEnvironment),
            cancellationToken);

        return recoveryCodes;
    }

    public async Task<bool> VerifyAsync(
        string userId,
        string provider,
        string code,
        string? purpose = null,
        CancellationToken cancellationToken = default)
    {
        var user = await ResolveUserAsync(userId, cancellationToken);

        if (!user.MfaEnabled)
        {
            return false;
        }

        var normalizedProvider = provider?.Trim().ToLowerInvariant() ?? string.Empty;

        return normalizedProvider switch
        {
            MfaProviders.AuthenticatorApp => user.TotpEnabled &&
                totpService.VerifyCode(user.TotpSecretCiphertext, code),

            MfaProviders.EmailOtp => user.EmailOtpEnabled &&
                !string.IsNullOrWhiteSpace(purpose) &&
                await emailOtpService.VerifyAsync(user.Id, purpose, code, cancellationToken),

            _ => false
        };
    }

    public async Task<bool> TryRedeemRecoveryCodeAsync(
        string userId,
        string recoveryCode,
        CancellationToken cancellationToken = default)
    {
        var user = await ResolveUserAsync(userId, cancellationToken);
        return await TryRedeemRecoveryCodeCoreAsync(user, recoveryCode, cancellationToken);
    }

    private async Task<ApplicationUser> ResolveUserAsync(string userId, CancellationToken cancellationToken)
    {
        var normalizedUserId = NormalizeRequired(userId, nameof(userId));

        return await dbContext.Users.SingleOrDefaultAsync(entity => entity.Id == normalizedUserId, cancellationToken)
            ?? throw new InvalidOperationException($"User '{normalizedUserId}' was not found.");
    }

    private async Task<string> ResetAuthenticatorSetupAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var resetResult = await userManager.ResetAuthenticatorKeyAsync(user);
        EnsureSucceeded(resetResult, "Authenticator setup could not be created.");

        var authenticatorKey = await userManager.GetAuthenticatorKeyAsync(user);

        if (string.IsNullOrWhiteSpace(authenticatorKey))
        {
            throw new InvalidOperationException("Authenticator key could not be resolved after reset.");
        }

        user.TotpSecretCiphertext = totpService.ProtectSecret(authenticatorKey);
        user.TotpEnabled = false;
        user.MfaEnabled = false;
        user.TwoFactorEnabled = false;
        user.PreferredMfaProvider = null;

        await UpdateUserAsync(user, cancellationToken);
        return authenticatorKey;
    }

    private async Task<bool> ValidateManagementCodeAsync(
        ApplicationUser user,
        string verificationCode,
        CancellationToken cancellationToken)
    {
        if (user.TotpEnabled && totpService.VerifyCode(user.TotpSecretCiphertext, verificationCode))
        {
            return true;
        }

        return await TryRedeemRecoveryCodeCoreAsync(user, verificationCode, cancellationToken);
    }

    private async Task<bool> TryRedeemRecoveryCodeCoreAsync(
        ApplicationUser user,
        string recoveryCode,
        CancellationToken cancellationToken)
    {
        var normalizedCode = NormalizeRecoveryCode(recoveryCode);

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return false;
        }

        var activeCodes = await dbContext.MfaRecoveryCodes
            .Where(entity =>
                entity.UserId == user.Id &&
                entity.ConsumedAtUtc == null &&
                !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var entry in activeCodes)
        {
            var verificationResult = passwordHasher.VerifyHashedPassword(user, entry.CodeHash, normalizedCode);

            if (verificationResult is not PasswordVerificationResult.Success and not PasswordVerificationResult.SuccessRehashNeeded)
            {
                continue;
            }

            if (verificationResult == PasswordVerificationResult.SuccessRehashNeeded)
            {
                entry.CodeHash = passwordHasher.HashPassword(user, normalizedCode);
            }

            entry.ConsumedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }

    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        await RemoveRecoveryCodesAsync(user.Id, cancellationToken);

        var recoveryCodes = new List<string>(RecoveryCodeCount);

        for (var index = 0; index < RecoveryCodeCount; index++)
        {
            var recoveryCode = CreateRecoveryCode();
            recoveryCodes.Add(recoveryCode);
            dbContext.MfaRecoveryCodes.Add(
                new MfaRecoveryCode
                {
                    UserId = user.Id,
                    CodeHash = passwordHasher.HashPassword(user, NormalizeRecoveryCode(recoveryCode))
                });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return recoveryCodes;
    }

    private async Task RemoveRecoveryCodesAsync(string userId, CancellationToken cancellationToken)
    {
        var existingCodes = await dbContext.MfaRecoveryCodes
            .Where(entity => entity.UserId == userId && !entity.IsDeleted)
            .ToListAsync(cancellationToken);

        if (existingCodes.Count == 0)
        {
            return;
        }

        dbContext.MfaRecoveryCodes.RemoveRange(existingCodes);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpdateUserAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        var updateResult = await userManager.UpdateAsync(user);
        EnsureSucceeded(updateResult, "User MFA state could not be updated.");
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private MfaStatusSnapshot MapStatus(ApplicationUser user, int activeRecoveryCodeCount)
    {
        return new MfaStatusSnapshot(
            user.MfaEnabled,
            user.TotpEnabled,
            user.EmailOtpEnabled,
            user.PreferredMfaProvider,
            HasPendingAuthenticatorEnrollment(user),
            activeRecoveryCodeCount,
            user.MfaUpdatedAtUtc);
    }

    private string BuildAuthenticatorUri(ApplicationUser user, string authenticatorKey)
    {
        var issuer = userManager.Options.Tokens.AuthenticatorIssuer;
        var accountLabel = user.Email ?? user.UserName ?? user.Id;
        var encodedIssuer = Uri.EscapeDataString(issuer);
        var encodedAccountLabel = Uri.EscapeDataString(accountLabel);

        return $"otpauth://totp/{encodedIssuer}:{encodedAccountLabel}?secret={authenticatorKey}&issuer={encodedIssuer}&digits=6&period=30";
    }

    private static bool HasPendingAuthenticatorEnrollment(ApplicationUser user)
    {
        return !user.TotpEnabled && !string.IsNullOrWhiteSpace(user.TotpSecretCiphertext);
    }

    private static string FormatSharedKey(string authenticatorKey)
    {
        var normalizedKey = NormalizeRequired(authenticatorKey, nameof(authenticatorKey)).ToUpperInvariant();
        var builder = new StringBuilder(normalizedKey.Length + normalizedKey.Length / 4);

        for (var index = 0; index < normalizedKey.Length; index += 4)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            var chunkLength = Math.Min(4, normalizedKey.Length - index);
            builder.Append(normalizedKey, index, chunkLength);
        }

        return builder.ToString();
    }

    private static string CreateRecoveryCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(5);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        Span<char> buffer = stackalloc char[8];
        var bitBuffer = 0;
        var bitsLeft = 0;
        var outputIndex = 0;

        foreach (var currentByte in bytes)
        {
            bitBuffer = (bitBuffer << 8) | currentByte;
            bitsLeft += 8;

            while (bitsLeft >= 5 && outputIndex < buffer.Length)
            {
                buffer[outputIndex++] = alphabet[(bitBuffer >> (bitsLeft - 5)) & 0x1F];
                bitsLeft -= 5;
            }
        }

        return string.Create(
            9,
            buffer.ToArray(),
            static (destination, state) =>
            {
                state.AsSpan(0, 4).CopyTo(destination);
                destination[4] = '-';
                state.AsSpan(4, 4).CopyTo(destination[5..]);
            });
    }

    private static void EnsureSucceeded(IdentityResult result, string message)
    {
        if (result.Succeeded)
        {
            return;
        }

        throw new InvalidOperationException(message);
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

    private static string NormalizeRecoveryCode(string recoveryCode)
    {
        return new string((recoveryCode ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }
}
