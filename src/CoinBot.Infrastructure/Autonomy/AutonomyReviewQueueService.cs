using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class AutonomyReviewQueueService(
    ApplicationDbContext dbContext,
    IAdminAuditLogService adminAuditLogService,
    TimeProvider timeProvider) : IAutonomyReviewQueueService
{
    public async Task<AutonomyReviewQueueItem> EnqueueAsync(
        AutonomyReviewQueueEnqueueRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedApprovalId = NormalizeRequired(request.ApprovalId, 128, nameof(request.ApprovalId));
        var normalizedScopeKey = NormalizeRequired(request.ScopeKey, 256, nameof(request.ScopeKey));
        var normalizedSuggestedAction = NormalizeRequired(request.SuggestedAction, 128, nameof(request.SuggestedAction));
        var normalizedReason = NormalizeRequired(request.Reason, 512, nameof(request.Reason));
        var normalizedCorrelationId = NormalizeOptional(request.CorrelationId, 128);
        var normalizedAffectedUsers = NormalizeUsers(request.AffectedUsers);
        var normalizedAffectedSymbols = NormalizeSymbols(request.AffectedSymbols);
        var expiresAtUtc = request.ExpiresAtUtc.ToUniversalTime();
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var existingPendingEntry = await dbContext.AutonomyReviewQueueEntries
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity =>
                    !entity.IsDeleted &&
                    entity.Status == AutonomyReviewStatus.Pending &&
                    entity.ScopeKey == normalizedScopeKey &&
                    entity.SuggestedAction == normalizedSuggestedAction &&
                    entity.Reason == normalizedReason &&
                    entity.ExpiresAtUtc > nowUtc,
                cancellationToken);

        if (existingPendingEntry is not null)
        {
            return Map(existingPendingEntry);
        }

        var existingEntry = await dbContext.AutonomyReviewQueueEntries
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(entity => !entity.IsDeleted && entity.ApprovalId == normalizedApprovalId, cancellationToken);

        if (existingEntry is not null)
        {
            return Map(existingEntry);
        }

        var entity = new AutonomyReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            ApprovalId = normalizedApprovalId,
            ScopeKey = normalizedScopeKey,
            SuggestedAction = normalizedSuggestedAction,
            ConfidenceScore = NormalizeConfidenceScore(request.ConfidenceScore),
            AffectedUsersCsv = string.Join(",", normalizedAffectedUsers),
            AffectedSymbolsCsv = string.Join(",", normalizedAffectedSymbols),
            ExpiresAtUtc = expiresAtUtc,
            Reason = normalizedReason,
            Status = AutonomyReviewStatus.Pending,
            CorrelationId = normalizedCorrelationId
        };

        dbContext.AutonomyReviewQueueEntries.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        await adminAuditLogService.WriteAsync(
            new AdminAuditLogWriteRequest(
                ActorUserId: "system:autonomy-review-queue",
                ActionType: "Autonomy.ReviewQueue.Enqueue",
                TargetType: "AutonomyReview",
                TargetId: entity.ApprovalId,
                OldValueSummary: null,
                NewValueSummary: BuildSummary(entity),
                Reason: entity.Reason,
                IpAddress: null,
                UserAgent: null,
                CorrelationId: entity.CorrelationId),
            cancellationToken);

        return Map(entity);
    }

    public async Task<IReadOnlyCollection<AutonomyReviewQueueItem>> ListAsync(
        AutonomyReviewStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.AutonomyReviewQueueEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => !entity.IsDeleted);

        if (status.HasValue)
        {
            query = query.Where(entity => entity.Status == status.Value);
        }

        var entries = await query
            .OrderBy(entity => entity.ExpiresAtUtc)
            .ThenBy(entity => entity.ApprovalId)
            .ToListAsync(cancellationToken);

        return entries.Select(Map).ToArray();
    }

    public async Task<int> ExpirePendingAsync(CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var expiredEntries = await dbContext.AutonomyReviewQueueEntries
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Status == AutonomyReviewStatus.Pending &&
                entity.ExpiresAtUtc <= nowUtc)
            .ToListAsync(cancellationToken);

        if (expiredEntries.Count == 0)
        {
            return 0;
        }

        foreach (var expiredEntry in expiredEntries)
        {
            expiredEntry.Status = AutonomyReviewStatus.Expired;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return expiredEntries.Count;
    }

    private static AutonomyReviewQueueItem Map(AutonomyReviewQueueEntry entity)
    {
        return new AutonomyReviewQueueItem(
            entity.ApprovalId,
            entity.ScopeKey,
            entity.SuggestedAction,
            entity.ConfidenceScore,
            SplitCsv(entity.AffectedUsersCsv),
            SplitCsv(entity.AffectedSymbolsCsv),
            entity.ExpiresAtUtc,
            entity.Reason,
            entity.Status,
            entity.CorrelationId);
    }

    private static IReadOnlyCollection<string> NormalizeUsers(IReadOnlyCollection<string> users)
    {
        ArgumentNullException.ThrowIfNull(users);

        return users
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyCollection<string> NormalizeSymbols(IReadOnlyCollection<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        return symbols
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyCollection<string> SplitCsv(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return Array.Empty<string>();
        }

        return csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static decimal NormalizeConfidenceScore(decimal value)
    {
        return Math.Clamp(value, 0m, 1m);
    }

    private static string BuildSummary(AutonomyReviewQueueEntry entity)
    {
        var summary =
            $"Scope={entity.ScopeKey}; Action={entity.SuggestedAction}; Confidence={entity.ConfidenceScore:0.####}; Users={entity.AffectedUsersCsv}; Symbols={entity.AffectedSymbolsCsv}; ExpiresAtUtc={entity.ExpiresAtUtc:O}; Status={entity.Status}";

        return summary.Length <= 2048
            ? summary
            : summary[..2048];
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
