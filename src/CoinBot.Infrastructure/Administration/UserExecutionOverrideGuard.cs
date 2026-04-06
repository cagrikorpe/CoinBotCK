using System.Globalization;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Application.Abstractions.Risk;
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
    IOptions<BotExecutionPilotOptions>? botExecutionPilotOptions = null) : IUserExecutionOverrideGuard
{
    private readonly BotExecutionPilotOptions optionsValue = botExecutionPilotOptions?.Value ?? new BotExecutionPilotOptions();

    public async Task<UserExecutionOverrideEvaluationResult> EvaluateAsync(
        UserExecutionOverrideEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedUserId = NormalizeRequired(request.UserId, nameof(request.UserId));
        var normalizedSymbol = NormalizeRequired(request.Symbol, nameof(request.Symbol)).ToUpperInvariant();
        var pilotContextRequested = IsPilotContextRequested(request.Context);
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

        if (request.Environment == ExecutionEnvironment.Live)
        {
            var resolution = await tradingModeResolver.ResolveAsync(
                new TradingModeResolutionRequest(
                    normalizedUserId,
                    request.BotId,
                    request.StrategyKey),
                cancellationToken);

            if (resolution.EffectiveMode != ExecutionEnvironment.Live &&
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
            var pilotBlockedReasons = EvaluatePilotConfiguration(request, normalizedUserId, normalizedSymbol);
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
        var isReduceOnlyOrder = await IsReduceOnlyOrderAsync(
            normalizedUserId,
            normalizedSymbol,
            request.Environment,
            request.Plane,
            request.Side,
            cancellationToken);

        if (!isReplacementOrder &&
            !isReduceOnlyOrder &&
            request.BotId.HasValue &&
            optionsValue.PerBotCooldownSeconds > 0 &&
            await HasRecentExecutionAsync(
                normalizedUserId,
                request.BotId,
                symbol: null,
                TimeSpan.FromSeconds(optionsValue.PerBotCooldownSeconds),
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
                request.CurrentExecutionOrderId,
                cancellationToken))
        {
            return Block(
                "UserExecutionSymbolCooldownActive",
                "Execution blocked because the symbol cooldown is still active.",
                guardSummary: pilotGuardSummary);
        }

        if (!isReplacementOrder &&
            !isReduceOnlyOrder &&
            optionsValue.MaxOpenPositionsPerUser > 0 &&
            await ResolveOpenPositionCountAsync(normalizedUserId, request.Environment, cancellationToken) >= optionsValue.MaxOpenPositionsPerUser)
        {
            return Block(
                "UserExecutionMaxOpenPositionsExceeded",
                "Execution blocked because the maximum open position limit has been reached.",
                guardSummary: pilotGuardSummary);
        }

        RiskVetoResult? riskEvaluation = null;
        if (riskPolicyEvaluator is not null &&
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
        else if (pilotContextRequested)
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
        string normalizedSymbol)
    {
        var blockedReasons = new List<string>();
        var allowedUserIds = optionsValue.AllowedUserIds
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var allowedBotIds = optionsValue.AllowedBotIds
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allowedSymbols = ResolveConfiguredPilotSymbols();
        var orderNotional = request.Quantity * request.Price;

        if (request.Environment != ExecutionEnvironment.Live)
        {
            blockedReasons.Add("UserExecutionPilotEnvironmentInvalid");
        }

        if (request.Plane != ExchangeDataPlane.Futures)
        {
            blockedReasons.Add("UserExecutionPilotPlaneInvalid");
        }

        if (allowedUserIds.Length != 1)
        {
            blockedReasons.Add("UserExecutionPilotAllowedUsersConfigurationInvalid");
        }
        else if (!string.Equals(allowedUserIds[0], normalizedUserId, StringComparison.Ordinal))
        {
            blockedReasons.Add("UserExecutionPilotUserNotAllowed");
        }

        if (!request.BotId.HasValue)
        {
            blockedReasons.Add("UserExecutionPilotBotRequired");
        }

        if (allowedBotIds.Length != 1)
        {
            blockedReasons.Add("UserExecutionPilotAllowedBotsConfigurationInvalid");
        }
        else if (request.BotId.HasValue &&
                 !string.Equals(allowedBotIds[0], request.BotId.Value.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            blockedReasons.Add("UserExecutionPilotBotNotAllowed");
        }

        if (allowedSymbols.Count != 1)
        {
            blockedReasons.Add("UserExecutionPilotAllowedSymbolsConfigurationInvalid");
        }
        else if (!allowedSymbols.Contains(normalizedSymbol))
        {
            blockedReasons.Add("UserExecutionPilotSymbolNotAllowed");
        }

        if (optionsValue.MaxOpenPositionsPerUser != 1)
        {
            blockedReasons.Add("UserExecutionPilotMaxOpenPositionsConfigurationInvalid");
        }

        if (optionsValue.PerBotCooldownSeconds <= 0 || optionsValue.PerSymbolCooldownSeconds <= 0)
        {
            blockedReasons.Add("UserExecutionPilotCooldownConfigurationInvalid");
        }

        if (optionsValue.MaxOrderNotional <= 0m)
        {
            blockedReasons.Add("UserExecutionPilotNotionalConfigurationMissing");
        }
        else if (orderNotional > optionsValue.MaxOrderNotional)
        {
            blockedReasons.Add("UserExecutionPilotNotionalLimitExceeded");
        }

        if (optionsValue.MaxDailyLossPercentage <= 0m)
        {
            blockedReasons.Add("UserExecutionPilotDailyLossConfigurationMissing");
        }

        return blockedReasons
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private HashSet<string> ResolveConfiguredPilotSymbols()
    {
        var configuredSymbols = optionsValue.AllowedSymbols
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToUpperInvariant())
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
        var allowedUserCount = optionsValue.AllowedUserIds.Count(item => !string.IsNullOrWhiteSpace(item));
        var allowedBotCount = optionsValue.AllowedBotIds.Count(item => !string.IsNullOrWhiteSpace(item));
        var allowedSymbolCount = ResolveConfiguredPilotSymbols().Count;
        var orderNotional = request.Quantity * request.Price;

        return $"PilotUserId={normalizedUserId}; PilotBotId={request.BotId?.ToString("N") ?? "missing"}; Symbol={normalizedSymbol}; Plane={request.Plane}; AllowedUserCount={allowedUserCount}; AllowedBotCount={allowedBotCount}; AllowedSymbolCount={allowedSymbolCount}; MaxOpenPositions={optionsValue.MaxOpenPositionsPerUser}; PerBotCooldownSeconds={optionsValue.PerBotCooldownSeconds}; PerSymbolCooldownSeconds={optionsValue.PerSymbolCooldownSeconds}; MaxOrderNotional={optionsValue.MaxOrderNotional.ToString("0.##", CultureInfo.InvariantCulture)}; RequestedNotional={orderNotional.ToString("0.##", CultureInfo.InvariantCulture)}; MaxDailyLossPercentage={optionsValue.MaxDailyLossPercentage.ToString("0.##", CultureInfo.InvariantCulture)}";
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
        return environment == ExecutionEnvironment.Demo
            ? await dbContext.DemoPositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .CountAsync(
                    entity => entity.OwnerUserId == userId &&
                              entity.Quantity != 0m &&
                              !entity.IsDeleted,
                    cancellationToken)
            : await dbContext.ExchangePositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .CountAsync(
                    entity => entity.OwnerUserId == userId &&
                              entity.Plane == ExchangeDataPlane.Futures &&
                              entity.Quantity != 0m &&
                              !entity.IsDeleted,
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
        ExecutionOrderSide side,
        CancellationToken cancellationToken)
    {
        if (environment == ExecutionEnvironment.Live &&
            plane == ExchangeDataPlane.Spot)
        {
            return false;
        }

        var netQuantity = environment == ExecutionEnvironment.Demo
            ? await dbContext.DemoPositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == userId &&
                    entity.Symbol == symbol &&
                    !entity.IsDeleted)
                .SumAsync(entity => entity.Quantity, cancellationToken)
            : await dbContext.ExchangePositions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == userId &&
                    entity.Plane == plane &&
                    entity.Symbol == symbol &&
                    !entity.IsDeleted)
                .SumAsync(entity => entity.Quantity, cancellationToken);

        if (netQuantity == 0m)
        {
            return false;
        }

        return netQuantity > 0m
            ? side == ExecutionOrderSide.Sell
            : side == ExecutionOrderSide.Buy;
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


