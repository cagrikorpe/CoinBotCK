using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Policy;

public sealed class GlobalPolicyEngine(
    ApplicationDbContext dbContext,
    IMemoryCache memoryCache,
    IAdminAuditLogService adminAuditLogService,
    TimeProvider timeProvider,
    ILogger<GlobalPolicyEngine> logger) : IGlobalPolicyEngine
{
    private static readonly object CacheKey = new();
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public Task<GlobalPolicySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return memoryCache.GetOrCreateAsync(
                CacheKey,
                async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(3);
                    return await LoadSnapshotAsync(cancellationToken);
                })!;
    }

    public async Task<GlobalPolicyEvaluationResult> EvaluateAsync(
        GlobalPolicyEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = await GetSnapshotAsync(cancellationToken);
        var normalizedSymbol = NormalizeSymbol(request.Symbol);
        var policy = snapshot.Policy;
        var restriction = policy.SymbolRestrictions.FirstOrDefault(item =>
            string.Equals(item.Symbol, normalizedSymbol, StringComparison.Ordinal));

        if (policy.AutonomyPolicy.Mode == AutonomyPolicyMode.ObserveOnly)
        {
            return Block(
                snapshot.CurrentVersion,
                "GlobalPolicyObserveOnly",
                "Global policy is in ObserveOnly mode and execution is blocked.",
                policy.AutonomyPolicy.Mode);
        }

        if (request.Environment == ExecutionEnvironment.Live &&
            (policy.AutonomyPolicy.Mode == AutonomyPolicyMode.ManualApprovalRequired ||
             policy.AutonomyPolicy.RequireManualApprovalForLive))
        {
            return Block(
                snapshot.CurrentVersion,
                "GlobalPolicyManualApprovalRequired",
                "Global policy requires manual approval before live execution.",
                policy.AutonomyPolicy.Mode);
        }

        if (restriction is not null)
        {
            switch (restriction.State)
            {
                case SymbolRestrictionState.Blocked:
                    return Block(
                        snapshot.CurrentVersion,
                        "GlobalPolicySymbolBlocked",
                        $"Execution blocked because {normalizedSymbol} is blocked by the global policy.",
                        policy.AutonomyPolicy.Mode,
                        restriction.State);
                case SymbolRestrictionState.CloseOnly:
                    if (!await IsReduceOnlyOrderAsync(request, cancellationToken))
                    {
                        return Block(
                            snapshot.CurrentVersion,
                            "GlobalPolicyCloseOnly",
                            $"Execution blocked because {normalizedSymbol} is close-only and new positions are not allowed.",
                            policy.AutonomyPolicy.Mode,
                            restriction.State);
                    }

                    break;
                case SymbolRestrictionState.ReduceOnly:
                    if (!await IsReduceOnlyOrderAsync(request, cancellationToken))
                    {
                        return Block(
                            snapshot.CurrentVersion,
                            "GlobalPolicyReduceOnly",
                            $"Execution blocked because {normalizedSymbol} is reduce-only and the order does not reduce exposure.",
                            policy.AutonomyPolicy.Mode,
                            restriction.State);
                    }

                    break;
                case SymbolRestrictionState.ReviewOnly:
                    return Advisory(
                        snapshot.CurrentVersion,
                        "GlobalPolicyReviewOnly",
                        $"Global policy marks {normalizedSymbol} as review-only.",
                        policy.AutonomyPolicy.Mode,
                        restriction.State);
            }
        }

        var orderNotional = request.Quantity * request.Price;

        if (policy.ExecutionGuardPolicy.MaxOrderNotional is decimal maxOrderNotional &&
            orderNotional > maxOrderNotional)
        {
            return Block(
                snapshot.CurrentVersion,
                "GlobalPolicyMaxOrderNotionalExceeded",
                $"Execution blocked because the order notional {orderNotional:0.##} exceeds the global cap {maxOrderNotional:0.##}.",
                policy.AutonomyPolicy.Mode);
        }

        if (policy.ExecutionGuardPolicy.MaxPositionNotional is decimal maxPositionNotional)
        {
            var projectedPositionNotional = await ResolveProjectedPositionNotionalAsync(request, cancellationToken);

            if (projectedPositionNotional > maxPositionNotional)
            {
                return Block(
                    snapshot.CurrentVersion,
                    "GlobalPolicyMaxPositionNotionalExceeded",
                    $"Execution blocked because the projected position notional {projectedPositionNotional:0.##} exceeds the global cap {maxPositionNotional:0.##}.",
                    policy.AutonomyPolicy.Mode);
            }
        }

        if (policy.ExecutionGuardPolicy.MaxDailyTrades is int maxDailyTrades &&
            maxDailyTrades >= 0 &&
            await ResolveTodayTradeCountAsync(request.UserId, cancellationToken) >= maxDailyTrades)
        {
            return Block(
                snapshot.CurrentVersion,
                "GlobalPolicyMaxDailyTradesExceeded",
                "Execution blocked because the global daily trade cap has been reached.",
                policy.AutonomyPolicy.Mode);
        }

        if (policy.AutonomyPolicy.Mode == AutonomyPolicyMode.RecommendOnly)
        {
            return Advisory(
                snapshot.CurrentVersion,
                "GlobalPolicyRecommendOnly",
                "Global policy is in RecommendOnly mode; execution is allowed but should be reviewed.",
                policy.AutonomyPolicy.Mode);
        }

        return new GlobalPolicyEvaluationResult(
            false,
            null,
            null,
            snapshot.CurrentVersion,
            restriction?.State,
            policy.AutonomyPolicy.Mode);
    }

    public async Task<GlobalPolicySnapshot> UpdateAsync(
        GlobalPolicyUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currentSnapshot = await LoadSnapshotAsync(cancellationToken);
        var normalizedPolicy = RiskPolicyDefaults.Normalize(request.Policy);

        if (PoliciesEquivalent(currentSnapshot.Policy, normalizedPolicy))
        {
            await WriteAuditAsync(
                request.ActorUserId,
                "GlobalPolicy.Update.NoChange",
                currentSnapshot.Policy.PolicyKey,
                request.Reason,
                request.CorrelationId,
                request.Source,
                request.IpAddress,
                request.UserAgent,
                oldValueSummary: BuildPolicySummary(currentSnapshot.Policy, request.Source),
                newValueSummary: BuildPolicySummary(normalizedPolicy, request.Source),
                cancellationToken);

            return currentSnapshot;
        }

        var trackedPolicy = await GetOrCreateTrackedPolicyAsync(cancellationToken);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var nextVersion = trackedPolicy.CurrentVersion + 1;
        var diffEntries = RiskPolicyDefaults.BuildDiff(currentSnapshot.Policy, normalizedPolicy);

        dbContext.RiskPolicyVersions.Add(RiskPolicyDefaults.CreateVersionEntity(
            trackedPolicy.Id,
            nextVersion,
            request.ActorUserId,
            utcNow,
            normalizedPolicy,
            request.Reason,
            diffEntries,
            request.Source,
            request.CorrelationId));

        trackedPolicy.CurrentVersion = nextVersion;
        trackedPolicy.PolicyJson = RiskPolicyDefaults.Serialize(normalizedPolicy);
        trackedPolicy.PolicyHash = ComputeHash(trackedPolicy.PolicyJson);
        trackedPolicy.LastUpdatedAtUtc = utcNow;
        trackedPolicy.LastUpdatedByUserId = request.ActorUserId;
        trackedPolicy.LastChangeSummary = request.Reason;

        await dbContext.SaveChangesAsync(cancellationToken);
        memoryCache.Remove(CacheKey);

        await WriteAuditAsync(
            request.ActorUserId,
            "GlobalPolicy.Update",
            trackedPolicy.PolicyKey,
            request.Reason,
            request.CorrelationId,
            request.Source,
            request.IpAddress,
            request.UserAgent,
            oldValueSummary: BuildPolicySummary(currentSnapshot.Policy, request.Source),
            newValueSummary: BuildPolicySummary(normalizedPolicy, request.Source),
            cancellationToken);

        logger.LogInformation("Global policy updated to version {Version} by {ActorUserId}.", nextVersion, request.ActorUserId);
        return await LoadSnapshotAsync(cancellationToken);
    }

    public async Task<GlobalPolicySnapshot> RollbackAsync(
        GlobalPolicyRollbackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currentSnapshot = await LoadSnapshotAsync(cancellationToken);
        var trackedPolicy = await GetOrCreateTrackedPolicyAsync(cancellationToken);
        var targetVersion = await dbContext.RiskPolicyVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.RiskPolicyId == trackedPolicy.Id &&
                          entity.Version == request.TargetVersion,
                cancellationToken)
            ?? throw new InvalidOperationException($"Policy version {request.TargetVersion} was not found.");

        if (targetVersion.Version == trackedPolicy.CurrentVersion)
        {
            return currentSnapshot;
        }

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var targetPolicy = RiskPolicyDefaults.DeserializePolicy(targetVersion.PolicyJson);
        var nextVersion = trackedPolicy.CurrentVersion + 1;
        var diffEntries = RiskPolicyDefaults.BuildDiff(currentSnapshot.Policy, targetPolicy);

        dbContext.RiskPolicyVersions.Add(RiskPolicyDefaults.CreateVersionEntity(
            trackedPolicy.Id,
            nextVersion,
            request.ActorUserId,
            utcNow,
            targetPolicy,
            request.Reason,
            diffEntries,
            request.Source,
            request.CorrelationId,
            rolledBackFromVersion: currentSnapshot.CurrentVersion));

        trackedPolicy.CurrentVersion = nextVersion;
        trackedPolicy.PolicyJson = RiskPolicyDefaults.Serialize(targetPolicy);
        trackedPolicy.PolicyHash = ComputeHash(trackedPolicy.PolicyJson);
        trackedPolicy.LastUpdatedAtUtc = utcNow;
        trackedPolicy.LastUpdatedByUserId = request.ActorUserId;
        trackedPolicy.LastChangeSummary = request.Reason;

        await dbContext.SaveChangesAsync(cancellationToken);
        memoryCache.Remove(CacheKey);

        await WriteAuditAsync(
            request.ActorUserId,
            "GlobalPolicy.Rollback",
            trackedPolicy.PolicyKey,
            request.Reason,
            request.CorrelationId,
            request.Source,
            request.IpAddress,
            request.UserAgent,
            oldValueSummary: BuildPolicySummary(currentSnapshot.Policy, request.Source),
            newValueSummary: BuildPolicySummary(targetPolicy, request.Source),
            cancellationToken);

        logger.LogWarning(
            "Global policy rolled back to version {TargetVersion} and republished as version {Version}.",
            request.TargetVersion,
            nextVersion);

        return await LoadSnapshotAsync(cancellationToken);
    }

    private async Task<GlobalPolicySnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var trackedPolicy = await GetOrCreateTrackedPolicyAsync(cancellationToken);
        var versions = await dbContext.RiskPolicyVersions
            .AsNoTracking()
            .Where(entity => entity.RiskPolicyId == trackedPolicy.Id)
            .OrderByDescending(entity => entity.Version)
            .Take(12)
            .ToListAsync(cancellationToken);

        var policy = RiskPolicyDefaults.DeserializePolicy(trackedPolicy.PolicyJson);

        return new GlobalPolicySnapshot(
            policy,
            trackedPolicy.CurrentVersion,
            trackedPolicy.LastUpdatedAtUtc,
            trackedPolicy.LastUpdatedByUserId,
            trackedPolicy.LastChangeSummary,
            true,
            versions.Select(MapVersionSnapshot).ToArray());
    }

    private async Task<RiskPolicy> GetOrCreateTrackedPolicyAsync(CancellationToken cancellationToken)
    {
        var trackedPolicy = await dbContext.RiskPolicies
            .SingleOrDefaultAsync(entity => entity.Id == RiskPolicyDefaults.SingletonId, cancellationToken);

        if (trackedPolicy is not null)
        {
            return trackedPolicy;
        }

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var defaultSnapshot = RiskPolicyDefaults.CreateDefaultSnapshot();
        trackedPolicy = RiskPolicyDefaults.CreateEntity(utcNow, defaultSnapshot);
        dbContext.RiskPolicies.Add(trackedPolicy);
        dbContext.RiskPolicyVersions.Add(RiskPolicyDefaults.CreateVersionEntity(
            trackedPolicy.Id,
            1,
            "system",
            utcNow,
            defaultSnapshot,
            "Initial policy",
            Array.Empty<GlobalPolicyDiffEntry>(),
            "PolicyDefaults"));
        await dbContext.SaveChangesAsync(cancellationToken);
        return trackedPolicy;
    }

    private async Task<bool> IsReduceOnlyOrderAsync(GlobalPolicyEvaluationRequest request, CancellationToken cancellationToken)
    {
        var currentNetQuantity = await ResolveCurrentNetQuantityAsync(request, cancellationToken);

        if (currentNetQuantity == 0m)
        {
            return false;
        }

        return currentNetQuantity > 0m
            ? request.Side == ExecutionOrderSide.Sell
            : request.Side == ExecutionOrderSide.Buy;
    }

    private async Task<decimal> ResolveProjectedPositionNotionalAsync(
        GlobalPolicyEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        var currentNetQuantity = await ResolveCurrentNetQuantityAsync(request, cancellationToken);
        var projectedQuantity = currentNetQuantity + (request.Side == ExecutionOrderSide.Buy ? request.Quantity : -request.Quantity);
        return Math.Abs(projectedQuantity) * request.Price;
    }

    private async Task<decimal> ResolveCurrentNetQuantityAsync(
        GlobalPolicyEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(request.Symbol);

        return request.Environment == ExecutionEnvironment.Demo
            ? await dbContext.DemoPositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == request.UserId &&
                    entity.Symbol == normalizedSymbol &&
                    !entity.IsDeleted)
                .SumAsync(entity => entity.Quantity, cancellationToken)
            : await dbContext.ExchangePositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == request.UserId &&
                    entity.Symbol == normalizedSymbol &&
                    !entity.IsDeleted)
                .SumAsync(entity => entity.Quantity, cancellationToken);
    }

    private async Task<int> ResolveTodayTradeCountAsync(string userId, CancellationToken cancellationToken)
    {
        var utcDayStart = timeProvider.GetUtcNow().UtcDateTime.Date;
        var utcDayEnd = utcDayStart.AddDays(1);

        return await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(
                entity => entity.OwnerUserId == userId &&
                          !entity.IsDeleted &&
                          entity.CreatedDate >= utcDayStart &&
                          entity.CreatedDate < utcDayEnd,
                cancellationToken);
    }

    private static GlobalPolicyVersionSnapshot MapVersionSnapshot(RiskPolicyVersion entity)
    {
        return new GlobalPolicyVersionSnapshot(
            entity.Version,
            entity.CreatedAtUtc,
            entity.CreatedByUserId,
            entity.ChangeSummary,
            RiskPolicyDefaults.DeserializeDiff(entity.DiffJson),
            RiskPolicyDefaults.DeserializePolicy(entity.PolicyJson),
            entity.Source,
            entity.CorrelationId,
            entity.RolledBackFromVersion);
    }

    private async Task WriteAuditAsync(
        string actorUserId,
        string actionType,
        string targetId,
        string reason,
        string? correlationId,
        string? source,
        string? ipAddress,
        string? userAgent,
        string? oldValueSummary,
        string? newValueSummary,
        CancellationToken cancellationToken)
    {
        _ = source;

        await adminAuditLogService.WriteAsync(
            new AdminAuditLogWriteRequest(
                actorUserId,
                actionType,
                "RiskPolicy",
                targetId,
                oldValueSummary,
                newValueSummary,
                reason,
                ipAddress,
                userAgent,
                correlationId),
            cancellationToken);
    }

    private static GlobalPolicyEvaluationResult Block(
        int policyVersion,
        string blockCode,
        string message,
        AutonomyPolicyMode autonomyMode,
        SymbolRestrictionState? matchedRestrictionState = null)
    {
        return new GlobalPolicyEvaluationResult(
            true,
            blockCode,
            message,
            policyVersion,
            matchedRestrictionState,
            autonomyMode);
    }

    private static GlobalPolicyEvaluationResult Advisory(
        int policyVersion,
        string advisoryCode,
        string message,
        AutonomyPolicyMode autonomyMode,
        SymbolRestrictionState? matchedRestrictionState = null)
    {
        return new GlobalPolicyEvaluationResult(
            false,
            null,
            null,
            policyVersion,
            matchedRestrictionState,
            autonomyMode,
            true,
            advisoryCode,
            message);
    }

    private static bool PoliciesEquivalent(RiskPolicySnapshot left, RiskPolicySnapshot right)
    {
        return RiskPolicyDefaults.Serialize(RiskPolicyDefaults.Normalize(left)) ==
               RiskPolicyDefaults.Serialize(RiskPolicyDefaults.Normalize(right));
    }

    private static string NormalizeSymbol(string? symbol)
    {
        var normalized = symbol?.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("The symbol is required.", nameof(symbol));
        }

        return normalized;
    }

    private static string BuildPolicySummary(RiskPolicySnapshot policy, string? source = null)
    {
        var summary =
            $"PolicyKey={policy.PolicyKey}; Autonomy={policy.AutonomyPolicy.Mode}; MaxOrderNotional={policy.ExecutionGuardPolicy.MaxOrderNotional?.ToString("0.##") ?? "none"}; MaxPositionNotional={policy.ExecutionGuardPolicy.MaxPositionNotional?.ToString("0.##") ?? "none"}; MaxDailyTrades={policy.ExecutionGuardPolicy.MaxDailyTrades?.ToString() ?? "none"}; Restrictions={policy.SymbolRestrictions.Count}";

        if (!string.IsNullOrWhiteSpace(source))
        {
            summary += $"; Source={source}";
        }

        return summary;
    }

    private static string ComputeHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash);
    }
}
