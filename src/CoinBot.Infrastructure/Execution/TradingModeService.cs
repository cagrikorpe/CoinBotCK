using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Execution;

public sealed class TradingModeService(
    ApplicationDbContext dbContext,
    IAuditLogService auditLogService) : ITradingModeResolver, ITradingModeService
{
    public async Task<TradingModeResolution> ResolveAsync(
        TradingModeResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        var globalState = await GetGlobalModeStateAsync(cancellationToken);
        var resolvedUserId = NormalizeOptional(request.UserId);
        TradingBot? bot = null;

        if (request.BotId is Guid botId)
        {
            bot = await dbContext.TradingBots
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(entity => entity.Id == botId && !entity.IsDeleted, cancellationToken);

            if (bot is null)
            {
                return CreateGuardedResolution(
                    globalState.DefaultMode,
                    reason: "Bot scope could not be resolved; effective mode is forced to Demo.");
            }

            resolvedUserId = bot.OwnerUserId;
        }

        ApplicationUser? user = null;

        if (!string.IsNullOrWhiteSpace(resolvedUserId))
        {
            user = await dbContext.Users
                .AsNoTracking()
                .SingleOrDefaultAsync(entity => entity.Id == resolvedUserId, cancellationToken);

            if (user is null)
            {
                return CreateGuardedResolution(
                    globalState.DefaultMode,
                    reason: "User scope could not be resolved; effective mode is forced to Demo.");
            }
        }

        var strategyKey = NormalizeOptional(request.StrategyKey) ?? NormalizeOptional(bot?.StrategyKey);
        var baseMode = bot?.TradingModeOverride
            ?? user?.TradingModeOverride
            ?? globalState.DefaultMode;
        var baseSource = ResolveBaseSource(bot, user);
        var hasExplicitLiveApproval = baseMode != ExecutionEnvironment.Live || HasLiveApproval(baseSource, globalState, user, bot);

        if (baseMode == ExecutionEnvironment.Live && !hasExplicitLiveApproval)
        {
            return new TradingModeResolution(
                globalState.DefaultMode,
                user?.TradingModeOverride,
                bot?.TradingModeOverride,
                StrategyPublishedMode: null,
                EffectiveMode: ExecutionEnvironment.Demo,
                ResolutionSource: TradingModeResolutionSource.LiveApprovalGuard,
                Reason: $"{DescribeSource(baseSource)} resolves to Live but explicit live approval is missing; effective mode is forced to Demo.",
                HasExplicitLiveApproval: false);
        }

        if (baseMode == ExecutionEnvironment.Live && !string.IsNullOrWhiteSpace(strategyKey))
        {
            if (string.IsNullOrWhiteSpace(resolvedUserId))
            {
                return CreateGuardedResolution(
                    globalState.DefaultMode,
                    user?.TradingModeOverride,
                    bot?.TradingModeOverride,
                    reason: "Strategy owner scope could not be resolved; effective mode is forced to Demo.",
                    hasExplicitLiveApproval);
            }

            var strategy = await dbContext.TradingStrategies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    entity => entity.OwnerUserId == resolvedUserId &&
                              entity.StrategyKey == strategyKey &&
                              !entity.IsDeleted,
                    cancellationToken);

            if (!IsStrategyLiveEligible(strategy))
            {
                return new TradingModeResolution(
                    globalState.DefaultMode,
                    user?.TradingModeOverride,
                    bot?.TradingModeOverride,
                    strategy?.PublishedMode,
                    EffectiveMode: ExecutionEnvironment.Demo,
                    ResolutionSource: TradingModeResolutionSource.StrategyPromotionGuard,
                    Reason: $"Strategy '{strategyKey}' is not promoted to Live; effective mode is forced to Demo.",
                    HasExplicitLiveApproval: hasExplicitLiveApproval);
            }

            return new TradingModeResolution(
                globalState.DefaultMode,
                user?.TradingModeOverride,
                bot?.TradingModeOverride,
                strategy!.PublishedMode,
                EffectiveMode: ExecutionEnvironment.Live,
                ResolutionSource: baseSource,
                Reason: DescribeBaseReason(baseSource, ExecutionEnvironment.Live),
                HasExplicitLiveApproval: hasExplicitLiveApproval);
        }

        return new TradingModeResolution(
            globalState.DefaultMode,
            user?.TradingModeOverride,
            bot?.TradingModeOverride,
            StrategyPublishedMode: null,
            EffectiveMode: baseMode,
            ResolutionSource: baseSource,
            Reason: DescribeBaseReason(baseSource, baseMode),
            HasExplicitLiveApproval: hasExplicitLiveApproval);
    }

    public async Task<TradingModeResolution> SetUserTradingModeOverrideAsync(
        string userId,
        ExecutionEnvironment? modeOverride,
        string actor,
        TradingModeLiveApproval? liveApproval = null,
        string? context = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = NormalizeRequired(userId, nameof(userId));
        var user = await dbContext.Users.SingleOrDefaultAsync(entity => entity.Id == normalizedUserId, cancellationToken)
            ?? throw new InvalidOperationException($"Trading mode override cannot be updated because user '{normalizedUserId}' was not found.");

        var globalState = await GetGlobalModeStateAsync(cancellationToken);
        var beforeMode = user.TradingModeOverride ?? globalState.DefaultMode;
        var afterMode = modeOverride ?? globalState.DefaultMode;
        var overrideChanged = user.TradingModeOverride != modeOverride;

        if (beforeMode != afterMode &&
            await UserHasOpenExposureBlockingModeChangeAsync(normalizedUserId, cancellationToken))
        {
            await WriteAuditAsync(
                actor,
                "TradingMode.UserOverride.Set",
                $"TradingMode/User/{normalizedUserId}",
                context,
                correlationId,
                "Blocked:OpenExposurePresent",
                afterMode.ToString(),
                cancellationToken);

            throw new InvalidOperationException("User trading mode cannot be changed while an inherited bot has open orders or positions.");
        }

        if (modeOverride == ExecutionEnvironment.Live &&
            (user.TradingModeOverride != ExecutionEnvironment.Live || user.TradingModeApprovedAtUtc is null))
        {
            var approval = await RequireLiveApprovalAsync(
                liveApproval,
                actor,
                action: "TradingMode.UserOverride.Set",
                target: $"TradingMode/User/{normalizedUserId}",
                context,
                correlationId,
                cancellationToken);

            user.TradingModeApprovedAtUtc = approval.ApprovedAtUtc;
            user.TradingModeApprovalReference = approval.ApprovalReference;
        }
        else if (user.TradingModeOverride == ExecutionEnvironment.Live || modeOverride != ExecutionEnvironment.Live)
        {
            user.TradingModeApprovedAtUtc = null;
            user.TradingModeApprovalReference = null;
        }

        user.TradingModeOverride = modeOverride;
        await dbContext.SaveChangesAsync(cancellationToken);

        var resolution = await ResolveAsync(
            new TradingModeResolutionRequest(UserId: normalizedUserId),
            cancellationToken);

        await WriteAuditAsync(
            actor,
            "TradingMode.UserOverride.Set",
            $"TradingMode/User/{normalizedUserId}",
            context,
            correlationId,
            overrideChanged ? "Applied" : "NoChange",
            resolution.EffectiveMode.ToString(),
            cancellationToken);

        return resolution;
    }

    public async Task<TradingModeResolution> SetBotTradingModeOverrideAsync(
        Guid botId,
        ExecutionEnvironment? modeOverride,
        string actor,
        TradingModeLiveApproval? liveApproval = null,
        string? context = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var bot = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(entity => entity.Id == botId && !entity.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Trading mode override cannot be updated because bot '{botId}' was not found.");

        var globalState = await GetGlobalModeStateAsync(cancellationToken);
        var userOverride = await dbContext.Users
            .AsNoTracking()
            .Where(entity => entity.Id == bot.OwnerUserId)
            .Select(entity => entity.TradingModeOverride)
            .SingleOrDefaultAsync(cancellationToken);
        var beforeMode = bot.TradingModeOverride ?? userOverride ?? globalState.DefaultMode;
        var afterMode = modeOverride ?? userOverride ?? globalState.DefaultMode;
        var overrideChanged = bot.TradingModeOverride != modeOverride;

        if (beforeMode != afterMode && HasOpenExposure(bot))
        {
            await WriteAuditAsync(
                actor,
                "TradingMode.BotOverride.Set",
                $"TradingMode/Bot/{botId}",
                context,
                correlationId,
                "Blocked:OpenExposurePresent",
                afterMode.ToString(),
                cancellationToken);

            throw new InvalidOperationException("Bot trading mode cannot be changed while open orders or positions exist.");
        }

        if (modeOverride == ExecutionEnvironment.Live &&
            (bot.TradingModeOverride != ExecutionEnvironment.Live || bot.TradingModeApprovedAtUtc is null))
        {
            var approval = await RequireLiveApprovalAsync(
                liveApproval,
                actor,
                action: "TradingMode.BotOverride.Set",
                target: $"TradingMode/Bot/{botId}",
                context,
                correlationId,
                cancellationToken);

            bot.TradingModeApprovedAtUtc = approval.ApprovedAtUtc;
            bot.TradingModeApprovalReference = approval.ApprovalReference;
        }
        else if (bot.TradingModeOverride == ExecutionEnvironment.Live || modeOverride != ExecutionEnvironment.Live)
        {
            bot.TradingModeApprovedAtUtc = null;
            bot.TradingModeApprovalReference = null;
        }

        bot.TradingModeOverride = modeOverride;
        await dbContext.SaveChangesAsync(cancellationToken);

        var resolution = await ResolveAsync(
            new TradingModeResolutionRequest(BotId: botId),
            cancellationToken);

        await WriteAuditAsync(
            actor,
            "TradingMode.BotOverride.Set",
            $"TradingMode/Bot/{botId}",
            context,
            correlationId,
            overrideChanged ? "Applied" : "NoChange",
            resolution.EffectiveMode.ToString(),
            cancellationToken);

        return resolution;
    }

    public async Task<TradingStrategyPromotionResult> PublishStrategyAsync(
        Guid strategyId,
        ExecutionEnvironment targetMode,
        string actor,
        TradingModeLiveApproval? liveApproval = null,
        string? context = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        var strategy = await dbContext.TradingStrategies
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(entity => entity.Id == strategyId && !entity.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException($"Trading strategy '{strategyId}' was not found.");
        var currentMode = strategy.PublishedMode;

        if (currentMode.HasValue &&
            currentMode.Value != targetMode &&
            await StrategyHasOpenExposureBlockingModeChangeAsync(strategy.OwnerUserId, strategy.StrategyKey, cancellationToken))
        {
            await WriteAuditAsync(
                actor,
                targetMode == ExecutionEnvironment.Live ? "TradingStrategy.PromoteLive" : "TradingStrategy.PublishDemo",
                $"TradingStrategy/{strategyId}",
                context,
                correlationId,
                "Blocked:OpenExposurePresent",
                targetMode.ToString(),
                cancellationToken);

            throw new InvalidOperationException("Strategy publish mode cannot be changed while linked bots have open orders or positions.");
        }

        if (targetMode == ExecutionEnvironment.Live)
        {
            if (strategy.PublishedMode != ExecutionEnvironment.Live &&
                (strategy.PublishedMode != ExecutionEnvironment.Demo || strategy.PromotionState != StrategyPromotionState.DemoPublished))
            {
                await WriteAuditAsync(
                    actor,
                    "TradingStrategy.PromoteLive",
                    $"TradingStrategy/{strategyId}",
                    context,
                    correlationId,
                    "Blocked:DemoPromotionRequired",
                    targetMode.ToString(),
                    cancellationToken);

                throw new InvalidOperationException("Strategy must be published to Demo before it can be promoted to Live.");
            }

            if (strategy.PublishedMode != ExecutionEnvironment.Live || strategy.LivePromotionApprovedAtUtc is null)
            {
                var approval = await RequireLiveApprovalAsync(
                    liveApproval,
                    actor,
                    action: "TradingStrategy.PromoteLive",
                    target: $"TradingStrategy/{strategyId}",
                    context,
                    correlationId,
                    cancellationToken,
                    requireDemoDataIsolationConfirmation: true);

                strategy.LivePromotionApprovedAtUtc = approval.ApprovedAtUtc;
                strategy.LivePromotionApprovalReference = approval.ApprovalReference;
            }
        }
        else
        {
            strategy.LivePromotionApprovedAtUtc = null;
            strategy.LivePromotionApprovalReference = null;
        }

        strategy.PublishedMode = targetMode;
        strategy.PublishedAtUtc = DateTime.UtcNow;
        strategy.PromotionState = targetMode == ExecutionEnvironment.Live
            ? StrategyPromotionState.LivePublished
            : StrategyPromotionState.DemoPublished;

        await dbContext.SaveChangesAsync(cancellationToken);

        var result = new TradingStrategyPromotionResult(
            strategy.Id,
            strategy.StrategyKey,
            strategy.PromotionState,
            strategy.PublishedMode,
            strategy.PublishedAtUtc,
            strategy.LivePromotionApprovedAtUtc.HasValue);

        await WriteAuditAsync(
            actor,
            targetMode == ExecutionEnvironment.Live ? "TradingStrategy.PromoteLive" : "TradingStrategy.PublishDemo",
            $"TradingStrategy/{strategyId}",
            context,
            correlationId,
            "Applied",
            targetMode.ToString(),
            cancellationToken);

        return result;
    }

    private async Task<GlobalModeState> GetGlobalModeStateAsync(CancellationToken cancellationToken)
    {
        var switchEntity = await dbContext.GlobalExecutionSwitches
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == GlobalExecutionSwitchDefaults.SingletonId && !entity.IsDeleted, cancellationToken);

        return switchEntity is null
            ? new GlobalModeState(ExecutionEnvironment.Demo, HasLiveApproval: false)
            : new GlobalModeState(
                switchEntity.DemoModeEnabled ? ExecutionEnvironment.Demo : ExecutionEnvironment.Live,
                switchEntity.LiveModeApprovedAtUtc.HasValue);
    }

    private static TradingModeResolutionSource ResolveBaseSource(TradingBot? bot, ApplicationUser? user)
    {
        if (bot?.TradingModeOverride is not null)
        {
            return TradingModeResolutionSource.BotOverride;
        }

        if (user?.TradingModeOverride is not null)
        {
            return TradingModeResolutionSource.UserOverride;
        }

        return TradingModeResolutionSource.GlobalDefault;
    }

    private static bool HasLiveApproval(
        TradingModeResolutionSource source,
        GlobalModeState globalState,
        ApplicationUser? user,
        TradingBot? bot)
    {
        return source switch
        {
            TradingModeResolutionSource.BotOverride => bot?.TradingModeApprovedAtUtc is not null,
            TradingModeResolutionSource.UserOverride => user?.TradingModeApprovedAtUtc is not null,
            _ => globalState.HasLiveApproval
        };
    }

    private async Task<bool> UserHasOpenExposureBlockingModeChangeAsync(string userId, CancellationToken cancellationToken)
    {
        return await dbContext.TradingBots
            .IgnoreQueryFilters()
            .AnyAsync(
                entity => entity.OwnerUserId == userId &&
                          !entity.IsDeleted &&
                          entity.TradingModeOverride == null &&
                          (entity.OpenOrderCount > 0 || entity.OpenPositionCount > 0),
                cancellationToken);
    }

    private async Task<bool> StrategyHasOpenExposureBlockingModeChangeAsync(
        string ownerUserId,
        string strategyKey,
        CancellationToken cancellationToken)
    {
        return await dbContext.TradingBots
            .IgnoreQueryFilters()
            .AnyAsync(
                entity => entity.OwnerUserId == ownerUserId &&
                          entity.StrategyKey == strategyKey &&
                          !entity.IsDeleted &&
                          (entity.OpenOrderCount > 0 || entity.OpenPositionCount > 0),
                cancellationToken);
    }

    private static bool HasOpenExposure(TradingBot bot)
    {
        return bot.OpenOrderCount > 0 || bot.OpenPositionCount > 0;
    }

    private static bool IsStrategyLiveEligible(TradingStrategy? strategy)
    {
        return strategy is not null &&
               strategy.PublishedMode == ExecutionEnvironment.Live &&
               strategy.PromotionState == StrategyPromotionState.LivePublished &&
               strategy.LivePromotionApprovedAtUtc.HasValue;
    }

    private async Task<TradingModeLiveApproval> RequireLiveApprovalAsync(
        TradingModeLiveApproval? liveApproval,
        string actor,
        string action,
        string target,
        string? context,
        string? correlationId,
        CancellationToken cancellationToken,
        bool requireDemoDataIsolationConfirmation = false)
    {
        var approvalReference = NormalizeOptional(liveApproval?.ApprovalReference);

        if (string.IsNullOrWhiteSpace(approvalReference))
        {
            await WriteAuditAsync(
                actor,
                action,
                target,
                context,
                correlationId,
                "Blocked:LiveApprovalRequired",
                ExecutionEnvironment.Live.ToString(),
                cancellationToken);

            throw new InvalidOperationException("Explicit live approval is required before switching to Live mode.");
        }

        if (requireDemoDataIsolationConfirmation && liveApproval?.ConfirmedDemoDataIsolation != true)
        {
            await WriteAuditAsync(
                actor,
                action,
                target,
                context,
                correlationId,
                "Blocked:DemoDataIsolationUnconfirmed",
                ExecutionEnvironment.Live.ToString(),
                cancellationToken);

            throw new InvalidOperationException("Live promotion requires an explicit confirmation that demo-only artifacts will not be carried into Live.");
        }

        if (approvalReference.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(liveApproval), "Approval reference cannot exceed 128 characters.");
        }

        return new TradingModeLiveApproval(
            approvalReference,
            liveApproval?.ApprovedAtUtc?.ToUniversalTime() ?? DateTime.UtcNow,
            liveApproval?.ConfirmedDemoDataIsolation ?? false);
    }

    private static string DescribeBaseReason(TradingModeResolutionSource source, ExecutionEnvironment mode)
    {
        return $"{DescribeSource(source)} resolves to {mode}.";
    }

    private static string DescribeSource(TradingModeResolutionSource source)
    {
        return source switch
        {
            TradingModeResolutionSource.BotOverride => "Bot override",
            TradingModeResolutionSource.UserOverride => "User override",
            _ => "Global default mode"
        };
    }

    private static TradingModeResolution CreateGuardedResolution(
        ExecutionEnvironment globalDefaultMode,
        string reason,
        bool hasExplicitLiveApproval = false)
    {
        return new TradingModeResolution(
            globalDefaultMode,
            UserOverrideMode: null,
            BotOverrideMode: null,
            StrategyPublishedMode: null,
            EffectiveMode: ExecutionEnvironment.Demo,
            ResolutionSource: TradingModeResolutionSource.ContextGuard,
            Reason: reason,
            HasExplicitLiveApproval: hasExplicitLiveApproval);
    }

    private static TradingModeResolution CreateGuardedResolution(
        ExecutionEnvironment globalDefaultMode,
        ExecutionEnvironment? userOverrideMode,
        ExecutionEnvironment? botOverrideMode,
        string reason,
        bool hasExplicitLiveApproval)
    {
        return new TradingModeResolution(
            globalDefaultMode,
            userOverrideMode,
            botOverrideMode,
            StrategyPublishedMode: null,
            EffectiveMode: ExecutionEnvironment.Demo,
            ResolutionSource: TradingModeResolutionSource.ContextGuard,
            Reason: reason,
            HasExplicitLiveApproval: hasExplicitLiveApproval);
    }

    private async Task WriteAuditAsync(
        string actor,
        string action,
        string target,
        string? context,
        string? correlationId,
        string outcome,
        string environment,
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
                environment),
            cancellationToken);
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalizedValue = NormalizeOptional(value);

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private sealed record GlobalModeState(ExecutionEnvironment DefaultMode, bool HasLiveApproval);
}
