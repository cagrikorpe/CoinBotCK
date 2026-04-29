using System.Globalization;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Execution;

public sealed class UserExecutionOverrideGuard(
    ApplicationDbContext dbContext,
    ITradingModeResolver tradingModeResolver,
    IGlobalPolicyEngine? globalPolicyEngine = null,
    ILogger<UserExecutionOverrideGuard>? logger = null,
    IHostEnvironment? hostEnvironment = null,
    IRiskPolicyEvaluator? riskPolicyEvaluator = null,
    IOptions<BotExecutionPilotOptions>? botExecutionPilotOptions = null,
    IOptions<ExecutionRuntimeOptions>? executionRuntimeOptions = null) : IUserExecutionOverrideGuard
{
    private static readonly ExecutionOrderState[] ActiveExecutionOrderStates =
    [
        ExecutionOrderState.Received,
        ExecutionOrderState.GatePassed,
        ExecutionOrderState.Dispatching,
        ExecutionOrderState.Submitted,
        ExecutionOrderState.PartiallyFilled,
        ExecutionOrderState.CancelRequested
    ];

    private const string RiskConcurrencyMaxOpenPositionsExceededCode = "RiskConcurrencyMaxOpenPositionsExceeded";
    private const string RiskConcurrencyMaxPendingOrdersExceededCode = "RiskConcurrencyMaxPendingOrdersExceeded";
    private const string RiskConcurrencyMaxSymbolExposureExceededCode = "RiskConcurrencyMaxSymbolExposureExceeded";
    private const string RiskConcurrencyMaxSymbolsExceededCode = "RiskConcurrencyMaxSymbolsExceeded";
    private const string RiskConcurrencyLimitAllowedCode = "RiskConcurrencyLimitAllowed";
    private const string RiskConcurrencyLimitSkippedForCloseOnlyCode = "RiskConcurrencyLimitSkippedForCloseOnly";

    private readonly BotExecutionPilotOptions optionsValue = botExecutionPilotOptions?.Value ?? new BotExecutionPilotOptions();
    private readonly ExecutionRuntimeOptions executionRuntimeOptionsValue = executionRuntimeOptions?.Value ?? new ExecutionRuntimeOptions();

    public async Task<UserExecutionOverrideEvaluationResult> EvaluateAsync(
        UserExecutionOverrideEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedUserId = NormalizeRequired(request.UserId, nameof(request.UserId));
        var normalizedSymbol = NormalizeRequired(request.Symbol, nameof(request.Symbol)).ToUpperInvariant();
        var pilotContextRequested = IsPilotContextRequested(request.Context);
        var isReduceOnlyOrder = request.ReduceOnly ??
            await IsReduceOnlyOrderAsync(
                normalizedUserId,
                normalizedSymbol,
                request.Environment,
                request.Plane,
                request.ExchangeAccountId,
                request.Side,
                cancellationToken);
        var pilotGuardSummary = pilotContextRequested
            ? BuildPilotGuardSummary(request, normalizedUserId, normalizedSymbol)
            : null;

        if (globalPolicyEngine is not null)
        {
            var policyEvaluation = await globalPolicyEngine.EvaluateAsync(
                new GlobalPolicyEvaluationRequest(
                    normalizedUserId,
                    normalizedSymbol,
                    request.Environment,
                    request.Side,
                    request.Quantity,
                    request.Price,
                    request.BotId,
                    request.StrategyKey),
                cancellationToken);

            if (policyEvaluation.IsBlocked)
            {
                return Block(
                    policyEvaluation.BlockCode ?? "GlobalPolicyBlocked",
                    policyEvaluation.Message ?? "Execution blocked by global policy.",
                    guardSummary: pilotGuardSummary);
            }

            if (policyEvaluation.IsAdvisory)
            {
                logger?.LogInformation(
                    "Global policy advisory for {Symbol} in {Environment}: {AdvisoryCode} - {AdvisoryMessage}",
                    normalizedSymbol,
                    request.Environment,
                    policyEvaluation.AdvisoryCode ?? "GlobalPolicyAdvisory",
                    policyEvaluation.AdvisoryMessage ?? "Global policy advisory raised.");
            }
        }

        if (ExecutionEnvironmentSemantics.IsLiveLike(request.Environment))
        {
            var resolution = await tradingModeResolver.ResolveAsync(
                new TradingModeResolutionRequest(
                    normalizedUserId,
                    request.BotId,
                    request.StrategyKey),
                cancellationToken);

            if (!ExecutionEnvironmentSemantics.IsLiveLike(resolution.EffectiveMode) &&
                !pilotContextRequested)
            {
                return Block(
                    "LiveProviderBlockedByResolvedDemoMode",
                    "Live provider access blocked because the effective mode resolved to Demo.",
                    guardSummary: pilotGuardSummary);
            }
        }

        if (pilotContextRequested)
        {
            var pilotBlockedReasons = EvaluatePilotConfiguration(request, normalizedUserId, normalizedSymbol, isReduceOnlyOrder);
            if (pilotBlockedReasons.Count > 0)
            {
                return Block(
                    pilotBlockedReasons,
                    $"Execution blocked because pilot scope constraints are not satisfied. BlockReasons={string.Join(",", pilotBlockedReasons)}; GuardSummary={pilotGuardSummary}",
                    guardSummary: pilotGuardSummary);
            }
        }

        if (await HasSameSymbolConflictAsync(normalizedUserId, request.BotId, normalizedSymbol, cancellationToken))
        {
            return Block(
                "UserExecutionSymbolConflict",
                "Execution blocked because multiple enabled bots share the same symbol for the same user.",
                guardSummary: pilotGuardSummary);
        }

        var isReplacementOrder = request.ReplacesExecutionOrderId.HasValue;

        if (!isReplacementOrder &&
            !isReduceOnlyOrder &&
            request.BotId.HasValue &&
            optionsValue.PerBotCooldownSeconds > 0 &&
            await HasRecentExecutionAsync(
                normalizedUserId,
                request.BotId,
                symbol: null,
                TimeSpan.FromSeconds(optionsValue.PerBotCooldownSeconds),
                request.Side,
                request.CurrentExecutionOrderId,
                cancellationToken))
        {
            return Block(
                "UserExecutionBotCooldownActive",
                "Execution blocked because the bot cooldown is still active.",
                guardSummary: pilotGuardSummary);
        }

        if (!isReplacementOrder &&
            !isReduceOnlyOrder &&
            optionsValue.PerSymbolCooldownSeconds > 0 &&
            await HasRecentExecutionAsync(
                normalizedUserId,
                botId: null,
                normalizedSymbol,
                TimeSpan.FromSeconds(optionsValue.PerSymbolCooldownSeconds),
                request.Side,
                request.CurrentExecutionOrderId,
                cancellationToken))
        {
            return Block(
                "UserExecutionSymbolCooldownActive",
                "Execution blocked because the symbol cooldown is still active.",
                guardSummary: pilotGuardSummary);
        }

        var concurrencyEvaluation = await EvaluateConcurrencyPolicyAsync(
            request,
            normalizedUserId,
            normalizedSymbol,
            isReduceOnlyOrder,
            cancellationToken);
        pilotGuardSummary = AppendGuardSummarySegment(pilotGuardSummary, concurrencyEvaluation.Summary);

        if (concurrencyEvaluation.IsBlocked)
        {
            return Block(
                concurrencyEvaluation.ReasonCode,
                concurrencyEvaluation.Message,
                guardSummary: pilotGuardSummary);
        }

        RiskVetoResult? riskEvaluation = null;
        if (!isReduceOnlyOrder &&
            riskPolicyEvaluator is not null &&
            request.TradingStrategyId.HasValue &&
            request.TradingStrategyVersionId.HasValue &&
            !string.IsNullOrWhiteSpace(request.Timeframe))
        {
            riskEvaluation = await riskPolicyEvaluator.EvaluateAsync(
                new RiskPolicyEvaluationRequest(
                    normalizedUserId,
                    request.TradingStrategyId.Value,
                    request.TradingStrategyVersionId.Value,
                    StrategySignalType.Entry,
                    request.Environment,
                    normalizedSymbol,
                    request.Timeframe.Trim(),
                    request.BotId,
                    request.Side,
                    request.Quantity,
                    request.Price),
                cancellationToken);

            if (riskEvaluation.IsVetoed)
            {
                return Block(
                    $"UserExecutionRisk{riskEvaluation.ReasonCode}",
                    $"Execution blocked because risk policy vetoed the order: {riskEvaluation.ReasonSummary}.",
                    riskEvaluation,
                    pilotGuardSummary);
            }

            if (pilotContextRequested &&
                riskEvaluation.Snapshot.CurrentDailyLossPercentage >= optionsValue.MaxDailyLossPercentage)
            {
                return Block(
                    ["UserExecutionPilotDailyLossLimitExceeded"],
                    $"Execution blocked because pilot daily loss limit is exceeded. CurrentDailyLossPercentage={riskEvaluation.Snapshot.CurrentDailyLossPercentage.ToString("0.##", CultureInfo.InvariantCulture)}; LimitPercentage={optionsValue.MaxDailyLossPercentage.ToString("0.##", CultureInfo.InvariantCulture)}; GuardSummary={pilotGuardSummary}",
                    riskEvaluation,
                    pilotGuardSummary);
            }
        }
        else if (pilotContextRequested && !isReduceOnlyOrder)
        {
            return Block(
                ["UserExecutionPilotRiskEvaluationUnavailable"],
                $"Execution blocked because pilot daily loss evaluation inputs are unavailable. GuardSummary={pilotGuardSummary}",
                guardSummary: pilotGuardSummary);
        }

        var overrideEntity = await dbContext.UserExecutionOverrides
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.UserId == normalizedUserId &&
                          !entity.IsDeleted,
                cancellationToken);

        if (overrideEntity is null)
        {
            return Allow(pilotGuardSummary);
        }

        if (overrideEntity.SessionDisabled)
        {
            return Block(
                "UserExecutionSessionDisabled",
                "Execution blocked because the user session is disabled by override.",
                guardSummary: pilotGuardSummary);
        }

        var allowedSymbols = ParseSymbols(overrideEntity.AllowedSymbolsCsv);

        if (allowedSymbols.Count > 0 && !allowedSymbols.Contains(normalizedSymbol))
        {
            return Block(
                "UserExecutionSymbolDenied",
                "Execution blocked because the symbol is outside the allow-list.",
                guardSummary: pilotGuardSummary);
        }

        var deniedSymbols = ParseSymbols(overrideEntity.DeniedSymbolsCsv);

        if (deniedSymbols.Contains(normalizedSymbol))
        {
            return Block(
                "UserExecutionSymbolDenied",
                "Execution blocked because the symbol is denied by override.",
                guardSummary: pilotGuardSummary);
        }

        if (overrideEntity.MaxOrderSize is decimal maxOrderSize &&
            (request.Quantity * request.Price) > maxOrderSize)
        {
            return Block(
                "UserExecutionMaxOrderSizeExceeded",
                "Execution blocked because the order notional exceeds the user override cap.",
                guardSummary: pilotGuardSummary);
        }

        if (overrideEntity.MaxDailyTrades is int maxDailyTrades &&
            maxDailyTrades >= 0 &&
            await ResolveTodayTradeCountAsync(normalizedUserId, cancellationToken) >= maxDailyTrades)
        {
            return Block(
                "UserExecutionMaxDailyTradesExceeded",
                "Execution blocked because the user daily trade cap has been reached.",
                guardSummary: pilotGuardSummary);
        }

        if (overrideEntity.ReduceOnly &&
            !isReduceOnlyOrder)
        {
            return Block(
                "UserExecutionReduceOnlyRequired",
                "Execution blocked because reduce-only mode is enabled for the user.",
                guardSummary: pilotGuardSummary);
        }

        return Allow(pilotGuardSummary);
    }

    private IReadOnlyList<string> EvaluatePilotConfiguration(
        UserExecutionOverrideEvaluationRequest request,
        string normalizedUserId,
        string normalizedSymbol,
        bool isReduceOnlyOrder)
    {
        var blockedReasons = new List<string>();
        var allowedUserIds = optionsValue.ResolveNormalizedAllowedUserIds();
        var allowedBotIds = optionsValue.ResolveNormalizedAllowedBotIds();
        var allowedSymbols = ResolveConfiguredPilotSymbols();

        if (!ExecutionEnvironmentSemantics.IsLiveLike(request.Environment))
        {
            blockedReasons.Add("UserExecutionPilotEnvironmentInvalid");
        }

        if (request.Plane != ExchangeDataPlane.Futures)
        {
            blockedReasons.Add("UserExecutionPilotPlaneInvalid");
        }

        if (allowedUserIds.Length == 0)
        {
            blockedReasons.Add("UserExecutionPilotUserScopeMissing");
        }
        else if (!allowedUserIds.Contains(normalizedUserId, StringComparer.Ordinal))
        {
            blockedReasons.Add("UserExecutionPilotUserNotAllowed");
        }

        if (allowedBotIds.Length == 0)
        {
            blockedReasons.Add("UserExecutionPilotBotScopeMissing");
        }

        if (!request.BotId.HasValue)
        {
            blockedReasons.Add("UserExecutionPilotBotRequired");
        }

        else if (allowedBotIds.Length > 0 &&
                 !allowedBotIds.Contains(request.BotId.Value.ToString("N"), StringComparer.OrdinalIgnoreCase))
        {
            blockedReasons.Add("UserExecutionPilotBotNotAllowed");
        }

        if (allowedSymbols.Count > 0 && !allowedSymbols.Contains(normalizedSymbol))
        {
            blockedReasons.Add("UserExecutionPilotSymbolNotAllowed");
        }

        if (optionsValue.MaxOpenPositionsPerUser != 1)
        {
            blockedReasons.Add("UserExecutionPilotMaxOpenPositionsConfigurationInvalid");
        }

        if (optionsValue.PerBotCooldownSeconds < 0 || optionsValue.PerSymbolCooldownSeconds < 0)
        {
            blockedReasons.Add("UserExecutionPilotCooldownConfigurationInvalid");
        }

        if (!isReduceOnlyOrder)
        {
            if (!TryResolvePilotOrderNotionalCap(out var maxPilotOrderNotional, out var notionalConfigurationReason))
            {
                blockedReasons.Add(notionalConfigurationReason!);
            }
            else if (!TryResolvePilotRequestedNotional(request, out var orderNotional, out var notionalDataReason))
            {
                blockedReasons.Add(notionalDataReason!);
            }
            else if (orderNotional > maxPilotOrderNotional)
            {
                blockedReasons.Add("UserExecutionPilotNotionalHardCapExceeded");
            }
        }

        if (optionsValue.MaxDailyLossPercentage <= 0m)
        {
            blockedReasons.Add("UserExecutionPilotDailyLossConfigurationMissing");
        }

        return blockedReasons
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private bool TryResolvePilotOrderNotionalCap(out decimal maxPilotOrderNotional, out string? blockedReason)
    {
        maxPilotOrderNotional = 0m;
        blockedReason = null;

        if (!optionsValue.HasConfiguredMaxPilotOrderNotional())
        {
            blockedReason = "UserExecutionPilotNotionalConfigurationMissing";
            return false;
        }

        if (!optionsValue.TryResolveMaxPilotOrderNotional(out maxPilotOrderNotional) || maxPilotOrderNotional <= 0m)
        {
            blockedReason = "UserExecutionPilotNotionalConfigurationInvalid";
            return false;
        }

        return true;
    }

    private static bool TryResolvePilotRequestedNotional(
        UserExecutionOverrideEvaluationRequest request,
        out decimal orderNotional,
        out string? blockedReason)
    {
        orderNotional = 0m;
        blockedReason = null;

        if (request.Quantity <= 0m || request.Price <= 0m)
        {
            blockedReason = "UserExecutionPilotNotionalDataUnavailable";
            return false;
        }

        orderNotional = request.Quantity * request.Price;

        if (orderNotional <= 0m)
        {
            blockedReason = "UserExecutionPilotNotionalDataUnavailable";
            return false;
        }

        return true;
    }

    private string ResolvePilotOrderNotionalCapSummary()
    {
        if (!optionsValue.HasConfiguredMaxPilotOrderNotional())
        {
            return "missing";
        }

        return optionsValue.TryResolveMaxPilotOrderNotional(out var maxPilotOrderNotional) && maxPilotOrderNotional > 0m
            ? maxPilotOrderNotional.ToString("0.##", CultureInfo.InvariantCulture)
            : $"invalid:{optionsValue.MaxPilotOrderNotional?.Trim()}";
    }

    private HashSet<string> ResolveConfiguredPilotSymbols()
    {
        var configuredSymbols = optionsValue.ResolveNormalizedAllowedSymbols()
            .ToHashSet(StringComparer.Ordinal);

        if (configuredSymbols.Count == 0)
        {
            configuredSymbols.Add(NormalizeSymbol(optionsValue.DefaultSymbol));
        }

        return configuredSymbols;
    }

    private string BuildPilotGuardSummary(
        UserExecutionOverrideEvaluationRequest request,
        string normalizedUserId,
        string normalizedSymbol)
    {
        var allowedUserCount = optionsValue.ResolveNormalizedAllowedUserIds().Length;
        var allowedBotCount = optionsValue.ResolveNormalizedAllowedBotIds().Length;
        var allowedSymbolCount = ResolveConfiguredPilotSymbols().Count;
        var requestedNotionalLabel = TryResolvePilotRequestedNotional(request, out var orderNotional, out _)
            ? orderNotional.ToString("0.##", CultureInfo.InvariantCulture)
            : "unavailable";

        return $"PilotUserId={normalizedUserId}; PilotBotId={request.BotId?.ToString("N") ?? "missing"}; Symbol={normalizedSymbol}; Plane={request.Plane}; AllowedUserCount={allowedUserCount}; AllowedBotCount={allowedBotCount}; AllowedSymbolCount={allowedSymbolCount}; MaxOpenPositions={optionsValue.MaxOpenPositionsPerUser}; PerBotCooldownSeconds={optionsValue.PerBotCooldownSeconds}; PerSymbolCooldownSeconds={optionsValue.PerSymbolCooldownSeconds}; MaxPilotOrderNotional={ResolvePilotOrderNotionalCapSummary()}; RequestedNotional={requestedNotionalLabel}; MaxDailyLossPercentage={optionsValue.MaxDailyLossPercentage.ToString("0.##", CultureInfo.InvariantCulture)}";
    }

    private async Task<ConcurrencyPolicyEvaluation> EvaluateConcurrencyPolicyAsync(
        UserExecutionOverrideEvaluationRequest request,
        string normalizedUserId,
        string normalizedSymbol,
        bool isReduceOnlyOrder,
        CancellationToken cancellationToken)
    {
        var openPositionStats = await ResolveOpenPositionConcurrencyStatsAsync(
            normalizedUserId,
            normalizedSymbol,
            request.Environment,
            request.Plane,
            cancellationToken);
        var pendingOrderStats = await ResolvePendingOrderConcurrencyStatsAsync(
            normalizedUserId,
            normalizedSymbol,
            request.Environment,
            request.Plane,
            request.CurrentExecutionOrderId,
            request.ReplacesExecutionOrderId,
            cancellationToken);

        if (isReduceOnlyOrder)
        {
            return ConcurrencyPolicyEvaluation.SkippedForCloseOnly(
                BuildConcurrencyGuardSummary(
                    openPositionStats,
                    pendingOrderStats,
                    "SkippedForCloseOnly",
                    RiskConcurrencyLimitSkippedForCloseOnlyCode,
                    skippedForCloseOnly: true));
        }

        if (optionsValue.MaxOpenPositionsPerUser > 0 &&
            openPositionStats.CurrentOpenPositionsPerUser >= optionsValue.MaxOpenPositionsPerUser)
        {
            return ConcurrencyPolicyEvaluation.Blocked(
                RiskConcurrencyMaxOpenPositionsExceededCode,
                "MaxOpenPositionsPerUserReached",
                $"Execution blocked because max open positions per user limit was reached. Current={openPositionStats.CurrentOpenPositionsPerUser}; Limit={optionsValue.MaxOpenPositionsPerUser}.",
                BuildConcurrencyGuardSummary(
                    openPositionStats,
                    pendingOrderStats,
                    "Blocked",
                    "MaxOpenPositionsPerUserReached"));
        }

        if (optionsValue.MaxOpenPositionsGlobal > 0 &&
            openPositionStats.CurrentOpenPositionsGlobal >= optionsValue.MaxOpenPositionsGlobal)
        {
            return ConcurrencyPolicyEvaluation.Blocked(
                RiskConcurrencyMaxOpenPositionsExceededCode,
                "MaxOpenPositionsGlobalReached",
                $"Execution blocked because max open positions global limit was reached. Current={openPositionStats.CurrentOpenPositionsGlobal}; Limit={optionsValue.MaxOpenPositionsGlobal}.",
                BuildConcurrencyGuardSummary(
                    openPositionStats,
                    pendingOrderStats,
                    "Blocked",
                    "MaxOpenPositionsGlobalReached"));
        }

        if (optionsValue.MaxOpenPositionsPerSymbol > 0 &&
            openPositionStats.CurrentOpenPositionsPerSymbol >= optionsValue.MaxOpenPositionsPerSymbol)
        {
            return ConcurrencyPolicyEvaluation.Blocked(
                RiskConcurrencyMaxSymbolExposureExceededCode,
                "MaxOpenPositionsPerSymbolReached",
                $"Execution blocked because max open positions per symbol limit was reached. Current={openPositionStats.CurrentOpenPositionsPerSymbol}; Limit={optionsValue.MaxOpenPositionsPerSymbol}; Symbol={normalizedSymbol}.",
                BuildConcurrencyGuardSummary(
                    openPositionStats,
                    pendingOrderStats,
                    "Blocked",
                    "MaxOpenPositionsPerSymbolReached"));
        }

        if (optionsValue.MaxSymbolsWithOpenPositionPerUser > 0 &&
            openPositionStats.CurrentSymbolsWithOpenPositionPerUser >= optionsValue.MaxSymbolsWithOpenPositionPerUser &&
            !openPositionStats.UserHasOpenPositionForSymbol)
        {
            return ConcurrencyPolicyEvaluation.Blocked(
                RiskConcurrencyMaxSymbolsExceededCode,
                "MaxSymbolsWithOpenPositionPerUserReached",
                $"Execution blocked because max symbols with open positions per user limit was reached. Current={openPositionStats.CurrentSymbolsWithOpenPositionPerUser}; Limit={optionsValue.MaxSymbolsWithOpenPositionPerUser}.",
                BuildConcurrencyGuardSummary(
                    openPositionStats,
                    pendingOrderStats,
                    "Blocked",
                    "MaxSymbolsWithOpenPositionPerUserReached"));
        }

        if (optionsValue.MaxPendingOrdersPerUser > 0 &&
            pendingOrderStats.CurrentPendingOrdersPerUser >= optionsValue.MaxPendingOrdersPerUser)
        {
            return ConcurrencyPolicyEvaluation.Blocked(
                RiskConcurrencyMaxPendingOrdersExceededCode,
                "MaxPendingOrdersPerUserReached",
                $"Execution blocked because max pending orders per user limit was reached. Current={pendingOrderStats.CurrentPendingOrdersPerUser}; Limit={optionsValue.MaxPendingOrdersPerUser}.",
                BuildConcurrencyGuardSummary(
                    openPositionStats,
                    pendingOrderStats,
                    "Blocked",
                    "MaxPendingOrdersPerUserReached"));
        }

        if (optionsValue.MaxConcurrentEntryOrdersPerUser > 0 &&
            pendingOrderStats.CurrentConcurrentEntryOrdersPerUser >= optionsValue.MaxConcurrentEntryOrdersPerUser)
        {
            return ConcurrencyPolicyEvaluation.Blocked(
                RiskConcurrencyMaxPendingOrdersExceededCode,
                "MaxConcurrentEntryOrdersPerUserReached",
                $"Execution blocked because max concurrent entry orders per user limit was reached. Current={pendingOrderStats.CurrentConcurrentEntryOrdersPerUser}; Limit={optionsValue.MaxConcurrentEntryOrdersPerUser}.",
                BuildConcurrencyGuardSummary(
                    openPositionStats,
                    pendingOrderStats,
                    "Blocked",
                    "MaxConcurrentEntryOrdersPerUserReached"));
        }

        if (optionsValue.MaxConcurrentEntryOrdersPerSymbol > 0 &&
            pendingOrderStats.CurrentConcurrentEntryOrdersPerSymbol >= optionsValue.MaxConcurrentEntryOrdersPerSymbol)
        {
            return ConcurrencyPolicyEvaluation.Blocked(
                RiskConcurrencyMaxPendingOrdersExceededCode,
                "MaxConcurrentEntryOrdersPerSymbolReached",
                $"Execution blocked because max concurrent entry orders per symbol limit was reached. Current={pendingOrderStats.CurrentConcurrentEntryOrdersPerSymbol}; Limit={optionsValue.MaxConcurrentEntryOrdersPerSymbol}; Symbol={normalizedSymbol}.",
                BuildConcurrencyGuardSummary(
                    openPositionStats,
                    pendingOrderStats,
                    "Blocked",
                    "MaxConcurrentEntryOrdersPerSymbolReached"));
        }

        return ConcurrencyPolicyEvaluation.Allowed(
            BuildConcurrencyGuardSummary(
                openPositionStats,
                pendingOrderStats,
                "Allowed",
                RiskConcurrencyLimitAllowedCode));
    }

    private string BuildConcurrencyGuardSummary(
        OpenPositionConcurrencyStats openPositionStats,
        PendingOrderConcurrencyStats pendingOrderStats,
        string decision,
        string reason,
        bool skippedForCloseOnly = false)
    {
        return FormattableString.Invariant(
            $"ConcurrencyPolicy=Applied; MaxOpenPositionsPerUser={optionsValue.MaxOpenPositionsPerUser}; CurrentOpenPositionsPerUser={openPositionStats.CurrentOpenPositionsPerUser}; MaxOpenPositionsGlobal={optionsValue.MaxOpenPositionsGlobal}; CurrentOpenPositionsGlobal={openPositionStats.CurrentOpenPositionsGlobal}; MaxOpenPositionsPerSymbol={optionsValue.MaxOpenPositionsPerSymbol}; CurrentOpenPositionsPerSymbol={openPositionStats.CurrentOpenPositionsPerSymbol}; MaxPendingOrdersPerUser={optionsValue.MaxPendingOrdersPerUser}; CurrentPendingOrdersPerUser={pendingOrderStats.CurrentPendingOrdersPerUser}; MaxConcurrentEntryOrdersPerUser={optionsValue.MaxConcurrentEntryOrdersPerUser}; CurrentConcurrentEntryOrdersPerUser={pendingOrderStats.CurrentConcurrentEntryOrdersPerUser}; MaxConcurrentEntryOrdersPerSymbol={optionsValue.MaxConcurrentEntryOrdersPerSymbol}; CurrentConcurrentEntryOrdersPerSymbol={pendingOrderStats.CurrentConcurrentEntryOrdersPerSymbol}; MaxSymbolsWithOpenPositionPerUser={optionsValue.MaxSymbolsWithOpenPositionPerUser}; CurrentSymbolsWithOpenPositionPerUser={openPositionStats.CurrentSymbolsWithOpenPositionPerUser}; ConcurrencyDecision={decision}; ConcurrencyReason={reason}; ConcurrencyLimitSkippedForCloseOnly={skippedForCloseOnly}");
    }

    private async Task<OpenPositionConcurrencyStats> ResolveOpenPositionConcurrencyStatsAsync(
        string userId,
        string normalizedSymbol,
        ExecutionEnvironment environment,
        ExchangeDataPlane plane,
        CancellationToken cancellationToken)
    {
        if (UsesInternalDemoExecution(environment))
        {
            var userPositions = await dbContext.DemoPositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == userId &&
                    entity.Quantity != 0m &&
                    !entity.IsDeleted)
                .Select(entity => entity.Symbol)
                .ToListAsync(cancellationToken);

            var demoCurrentOpenPositionsGlobal = await dbContext.DemoPositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .CountAsync(
                    entity => entity.Quantity != 0m &&
                              !entity.IsDeleted,
                    cancellationToken);

            var demoCurrentOpenPositionsPerSymbol = await dbContext.DemoPositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .CountAsync(
                    entity => entity.Quantity != 0m &&
                              !entity.IsDeleted &&
                              entity.Symbol == normalizedSymbol,
                    cancellationToken);

            return new OpenPositionConcurrencyStats(
                CurrentOpenPositionsPerUser: userPositions.Count,
                CurrentOpenPositionsGlobal: demoCurrentOpenPositionsGlobal,
                CurrentOpenPositionsPerSymbol: demoCurrentOpenPositionsPerSymbol,
                CurrentSymbolsWithOpenPositionPerUser: userPositions
                    .Select(NormalizePositionSymbol)
                    .Where(entity => entity.Length != 0)
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                UserHasOpenPositionForSymbol: userPositions.Any(entity =>
                    string.Equals(NormalizePositionSymbol(entity), normalizedSymbol, StringComparison.Ordinal)));
        }

        var projectedUserPositions = await LivePositionTruthResolver.ResolveProjectedPositionsAsync(
            dbContext,
            userId,
            plane,
            exchangeAccountId: null,
            cancellationToken);

        var currentOpenPositionsGlobal = await dbContext.ExchangePositions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(
                entity => entity.Plane == plane &&
                          entity.Quantity != 0m &&
                          !entity.IsDeleted,
                cancellationToken);

        var currentOpenPositionsPerSymbol = await dbContext.ExchangePositions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(
                entity => entity.Plane == plane &&
                          entity.Quantity != 0m &&
                          !entity.IsDeleted &&
                          entity.Symbol == normalizedSymbol,
                cancellationToken);

        return new OpenPositionConcurrencyStats(
            CurrentOpenPositionsPerUser: projectedUserPositions.Count,
            CurrentOpenPositionsGlobal: currentOpenPositionsGlobal,
            CurrentOpenPositionsPerSymbol: currentOpenPositionsPerSymbol,
            CurrentSymbolsWithOpenPositionPerUser: projectedUserPositions
                .Select(entity => entity.Symbol)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            UserHasOpenPositionForSymbol: projectedUserPositions.Any(entity =>
                string.Equals(entity.Symbol, normalizedSymbol, StringComparison.Ordinal)));
    }

    private async Task<PendingOrderConcurrencyStats> ResolvePendingOrderConcurrencyStatsAsync(
        string userId,
        string normalizedSymbol,
        ExecutionEnvironment environment,
        ExchangeDataPlane plane,
        Guid? currentExecutionOrderId,
        Guid? replacesExecutionOrderId,
        CancellationToken cancellationToken)
    {
        var activeOrders = await BuildActiveExecutionOrdersQuery(
                environment,
                plane,
                currentExecutionOrderId,
                replacesExecutionOrderId)
            .Where(entity => entity.OwnerUserId == userId)
            .Select(entity => new
            {
                entity.Symbol,
                entity.SignalType,
                entity.ReduceOnly
            })
            .ToListAsync(cancellationToken);

        var currentConcurrentEntryOrdersGlobalBySymbol = await BuildActiveExecutionOrdersQuery(
                environment,
                plane,
                currentExecutionOrderId,
                replacesExecutionOrderId)
            .CountAsync(
                entity => entity.SignalType == StrategySignalType.Entry &&
                          !entity.ReduceOnly &&
                          entity.Symbol == normalizedSymbol,
                cancellationToken);

        return new PendingOrderConcurrencyStats(
            CurrentPendingOrdersPerUser: activeOrders.Count,
            CurrentConcurrentEntryOrdersPerUser: activeOrders.Count(entity =>
                entity.SignalType == StrategySignalType.Entry &&
                !entity.ReduceOnly),
            CurrentConcurrentEntryOrdersPerSymbol: currentConcurrentEntryOrdersGlobalBySymbol);
    }

    private IQueryable<ExecutionOrder> BuildActiveExecutionOrdersQuery(
        ExecutionEnvironment environment,
        ExchangeDataPlane plane,
        Guid? currentExecutionOrderId,
        Guid? replacesExecutionOrderId)
    {
        var query = dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.Plane == plane &&
                !entity.IsDeleted &&
                ActiveExecutionOrderStates.Contains(entity.State) &&
                (!currentExecutionOrderId.HasValue || entity.Id != currentExecutionOrderId.Value) &&
                (!replacesExecutionOrderId.HasValue || entity.Id != replacesExecutionOrderId.Value));

        if (UsesInternalDemoExecution(environment))
        {
            return query.Where(entity => entity.ExecutionEnvironment == ExecutionEnvironment.Demo);
        }

        return query.Where(entity =>
            entity.ExecutionEnvironment == ExecutionEnvironment.Live ||
            entity.ExecutionEnvironment == ExecutionEnvironment.BinanceTestnet);
    }

    private static string? AppendGuardSummarySegment(string? summary, string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return string.IsNullOrWhiteSpace(summary) ? null : summary;
        }

        return string.IsNullOrWhiteSpace(summary)
            ? segment
            : $"{summary}; {segment}";
    }

    private async Task<bool> HasSameSymbolConflictAsync(
        string userId,
        Guid? currentBotId,
        string symbol,
        CancellationToken cancellationToken)
    {
        if (!currentBotId.HasValue)
        {
            return false;
        }

        var enabledBots = await dbContext.TradingBots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == userId &&
                entity.IsEnabled &&
                !entity.IsDeleted)
            .Select(entity => new { entity.Id, entity.Symbol })
            .ToListAsync(cancellationToken);

        return enabledBots
            .Where(entity => entity.Id != currentBotId.Value)
            .Select(entity => NormalizeSymbol(entity.Symbol))
            .Any(item => string.Equals(item, symbol, StringComparison.Ordinal));
    }

    private async Task<bool> HasRecentExecutionAsync(
        string userId,
        Guid? botId,
        string? symbol,
        TimeSpan cooldown,
        ExecutionOrderSide requestedSide,
        Guid? currentExecutionOrderId,
        CancellationToken cancellationToken)
    {
        var thresholdUtc = DateTime.UtcNow.Subtract(cooldown);
        var query = dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == userId &&
                entity.CooldownApplied &&
                entity.Side == requestedSide &&
                (!currentExecutionOrderId.HasValue || entity.Id != currentExecutionOrderId.Value) &&
                !entity.IsDeleted &&
                entity.CreatedDate >= thresholdUtc);

        if (botId.HasValue)
        {
            query = query.Where(entity => entity.BotId == botId.Value);
        }

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            query = query.Where(entity => entity.Symbol == symbol);
        }

        return await query.AnyAsync(cancellationToken);
    }

    private async Task<int> ResolveOpenPositionCountAsync(
        string userId,
        ExecutionEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (UsesInternalDemoExecution(environment))
        {
            return await dbContext.DemoPositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .CountAsync(
                    entity => entity.OwnerUserId == userId &&
                              entity.Quantity != 0m &&
                              !entity.IsDeleted,
                    cancellationToken);
        }

        return await LivePositionTruthResolver.ResolveOpenPositionCountAsync(
            dbContext,
            userId,
            ExchangeDataPlane.Futures,
            exchangeAccountId: null,
            cancellationToken);
    }

    private async Task<int> ResolveTodayTradeCountAsync(string userId, CancellationToken cancellationToken)
    {
        var utcDayStart = DateTime.UtcNow.Date;
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

    private async Task<bool> IsReduceOnlyOrderAsync(
        string userId,
        string symbol,
        ExecutionEnvironment environment,
        ExchangeDataPlane plane,
        Guid? exchangeAccountId,
        ExecutionOrderSide side,
        CancellationToken cancellationToken)
    {
        if (ExecutionEnvironmentSemantics.IsLiveLike(environment) &&
            plane == ExchangeDataPlane.Spot)
        {
            return false;
        }

        var normalizedSymbol = NormalizePositionSymbol(symbol);

        var netQuantity = UsesInternalDemoExecution(environment)
            ? await dbContext.DemoPositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == userId &&
                    entity.Symbol == normalizedSymbol &&
                    !entity.IsDeleted)
                .SumAsync(entity => entity.Quantity, cancellationToken)
            : await ResolveLiveNetQuantityAsync(userId, normalizedSymbol, plane, exchangeAccountId, cancellationToken);

        if (netQuantity == 0m)
        {
            return false;
        }

        return netQuantity > 0m
            ? side == ExecutionOrderSide.Sell
            : side == ExecutionOrderSide.Buy;
    }

    private async Task<decimal> ResolveLiveNetQuantityAsync(
        string userId,
        string normalizedSymbol,
        ExchangeDataPlane plane,
        Guid? exchangeAccountId,
        CancellationToken cancellationToken)
    {
        return await LivePositionTruthResolver.ResolveNetQuantityAsync(
            dbContext,
            userId,
            plane,
            exchangeAccountId,
            normalizedSymbol,
            cancellationToken);
    }

    private bool UsesInternalDemoExecution(ExecutionEnvironment environment)
    {
        return ExecutionEnvironmentSemantics.UsesInternalDemoExecution(
            environment,
            executionRuntimeOptionsValue.AllowInternalDemoExecution);
    }

    private static decimal ResolveSignedOrderQuantity(ExecutionOrder entity)
    {
        var quantity = entity.FilledQuantity > 0m
            ? entity.FilledQuantity
            : ResolvePendingExposureQuantity(entity);
        if (quantity == 0m)
        {
            return 0m;
        }

        return entity.Side == ExecutionOrderSide.Buy
            ? quantity
            : -quantity;
    }

    private static decimal ResolvePendingExposureQuantity(ExecutionOrder entity)
    {
        if (!entity.SubmittedToBroker ||
            entity.ReduceOnly ||
            entity.OrderType != ExecutionOrderType.Market)
        {
            return 0m;
        }

        return entity.State is ExecutionOrderState.Submitted or
            ExecutionOrderState.Dispatching or
            ExecutionOrderState.CancelRequested
            ? entity.Quantity
            : 0m;
    }

    private static decimal ResolveSignedPositionQuantity(ExchangePosition entity)
    {
        var quantity = entity.Quantity;
        if (quantity == 0m)
        {
            return 0m;
        }

        return NormalizePositionSide(entity.PositionSide) == "SHORT"
            ? -Math.Abs(quantity)
            : quantity;
    }

    private static string NormalizePositionSide(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "BOTH"
            : value.Trim().ToUpperInvariant();
    }

    private static string NormalizePositionSymbol(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToUpperInvariant();
    }

    private static HashSet<string> ParseSymbols(string? csv)
    {
        return string.IsNullOrWhiteSpace(csv)
            ? []
            : csv
                .Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.ToUpperInvariant())
                .ToHashSet(StringComparer.Ordinal);
    }

    private sealed record OpenPositionConcurrencyStats(
        int CurrentOpenPositionsPerUser,
        int CurrentOpenPositionsGlobal,
        int CurrentOpenPositionsPerSymbol,
        int CurrentSymbolsWithOpenPositionPerUser,
        bool UserHasOpenPositionForSymbol);

    private sealed record PendingOrderConcurrencyStats(
        int CurrentPendingOrdersPerUser,
        int CurrentConcurrentEntryOrdersPerUser,
        int CurrentConcurrentEntryOrdersPerSymbol);

    private sealed record ConcurrencyPolicyEvaluation(
        bool IsBlocked,
        string? ReasonCode,
        string Summary,
        string Message)
    {
        public static ConcurrencyPolicyEvaluation Allowed(string summary)
        {
            return new(false, null, summary, "Execution allowed because concurrency limits are within configured bounds.");
        }

        public static ConcurrencyPolicyEvaluation SkippedForCloseOnly(string summary)
        {
            return new(false, null, summary, "Execution allowed because reduce-only close-only requests bypass concurrency limits.");
        }

        public static ConcurrencyPolicyEvaluation Blocked(string code, string reason, string message, string summary)
        {
            return new(true, code, summary, message);
        }
    }

    private static UserExecutionOverrideEvaluationResult Allow(string? guardSummary = null)
    {
        return new UserExecutionOverrideEvaluationResult(false, null, null, null, null, guardSummary);
    }

    private static UserExecutionOverrideEvaluationResult Block(
        string reason,
        string message,
        RiskVetoResult? riskEvaluation = null,
        string? guardSummary = null)
    {
        return new UserExecutionOverrideEvaluationResult(true, reason, message, riskEvaluation, [reason], guardSummary);
    }

    private static UserExecutionOverrideEvaluationResult Block(
        IReadOnlyList<string> reasons,
        string message,
        RiskVetoResult? riskEvaluation = null,
        string? guardSummary = null)
    {
        var primaryReason = reasons.Count == 0 ? "Blocked" : reasons[0];
        return new UserExecutionOverrideEvaluationResult(true, primaryReason, message, riskEvaluation, reasons.ToArray(), guardSummary);
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

    private string NormalizeSymbol(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? optionsValue.DefaultSymbol.Trim().ToUpperInvariant()
            : value.Trim().ToUpperInvariant();
    }

    private static bool IsPilotContextRequested(string? context)
    {
        return TryReadBooleanFlag(context, "DevelopmentFuturesTestnetPilot");
    }
    private static bool TryReadBooleanFlag(string? context, string key)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return false;
        }

        var prefix = $"{key}=";
        var segments = context.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (!segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return bool.TryParse(segment[prefix.Length..].Trim(), out var value) && value;
        }

        return false;
    }
}
