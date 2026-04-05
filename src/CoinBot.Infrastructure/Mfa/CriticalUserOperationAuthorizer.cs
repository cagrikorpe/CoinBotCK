using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Mfa;

public sealed class CriticalUserOperationAuthorizer(
    ApplicationDbContext dbContext,
    IAuditLogService auditLogService) : ICriticalUserOperationAuthorizer
{
    private const string AuditEnvironment = "Identity";

    public async Task<CriticalUserOperationAuthorizationResult> AuthorizeAsync(
        CriticalUserOperationAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedActor = NormalizeRequired(request.Actor, nameof(request.Actor));
        var normalizedOperationKey = NormalizeRequired(request.OperationKey, nameof(request.OperationKey));
        var normalizedTarget = NormalizeRequired(request.Target, nameof(request.Target));

        string normalizedUserId;

        try
        {
            normalizedUserId = dbContext.EnsureCurrentUserScope(request.UserId);
        }
        catch (InvalidOperationException)
        {
            await WriteAuditAsync(
                normalizedActor,
                "Security.OwnershipViolation",
                normalizedTarget,
                $"Operation={normalizedOperationKey}; Reason=UserScopeMismatch",
                request.CorrelationId,
                "Denied",
                cancellationToken);

            return new CriticalUserOperationAuthorizationResult(
                false,
                "OwnershipViolation",
                "Istek aktif kullanici kapsamı disinda oldugu icin reddedildi.");
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == normalizedUserId, cancellationToken);

        if (user is null)
        {
            await WriteAuditAsync(
                normalizedActor,
                "Security.OwnershipViolation",
                normalizedTarget,
                $"Operation={normalizedOperationKey}; Reason=UserNotFound",
                request.CorrelationId,
                "Denied",
                cancellationToken);

            return new CriticalUserOperationAuthorizationResult(
                false,
                "UserNotFound",
                "Kullanici bulunamadi.");
        }

        if (!IsCriticalOperationMfaSatisfied(user))
        {
            await WriteAuditAsync(
                normalizedActor,
                "Security.MfaRequired",
                normalizedTarget,
                $"Operation={normalizedOperationKey}; PreferredProvider={NormalizeOptional(user.PreferredMfaProvider) ?? "none"}",
                request.CorrelationId,
                "Denied",
                cancellationToken);

            return new CriticalUserOperationAuthorizationResult(
                false,
                "MfaRequired",
                "Bu islem icin MFA zorunludur. Once MFA etkinlestirilmelidir.");
        }

        return new CriticalUserOperationAuthorizationResult(true, null, null);
    }

    private async Task WriteAuditAsync(
        string actor,
        string action,
        string target,
        string? context,
        string? correlationId,
        string outcome,
        CancellationToken cancellationToken)
    {
        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                actor,
                action,
                target,
                context,
                correlationId,
                outcome,
                AuditEnvironment),
            cancellationToken);
    }

    private static bool IsCriticalOperationMfaSatisfied(ApplicationUser user)
    {
        return user.MfaEnabled &&
               user.TwoFactorEnabled &&
               (user.TotpEnabled || user.EmailOtpEnabled);
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

    private static string? NormalizeOptional(string? value)
    {
        var normalizedValue = value?.Trim();
        return string.IsNullOrWhiteSpace(normalizedValue) ? null : normalizedValue;
    }
}
