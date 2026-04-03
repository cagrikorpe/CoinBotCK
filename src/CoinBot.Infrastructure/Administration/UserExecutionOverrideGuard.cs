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
                    policyEvaluation.Message ?? "Execution blocked by global policy.");
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
                !IsDevelopmentFuturesPilotOverrideAllowed(request.Context))
            {
                return Block(
                    "LiveProviderBlockedByResolvedDemoMode",
                    "Live provider access blocked because the effective mode resolved to Demo.");
            }
        }

        if (await HasSameSymbolConflictAsync(normalizedUserId, request.BotId, normalizedSymbol, cancellationToken))
        {
            return Block(
                "UserExecutionSymbolConflict",
                "Execution blocked because multiple enabled bots share the same symbol for the same user.");
        }

        var isReplacementOrder = request.ReplacesExecutionOrderId.HasValue;
        var isReduceOnlyOrder = await IsReduceOnlyOrderAsync(
            normalizedUserId,
            normalizedSymbol,
            request.Environment,
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
                "Execution blocked because the bot cooldown is still active.");
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
                "Execution blocked because the symbol cooldown is still active.");
        }

        if (!isReplacementOrder &&
            !isReduceOnlyOrder &&
            optionsValue.MaxOpenPositionsPerUser > 0 &&
            await ResolveOpenPositionCountAsync(normalizedUserId, request.Environment, cancellationToken) >= optionsValue.MaxOpenPositionsPerUser)
        {
            return Block(
                "UserExecutionMaxOpenPositionsExceeded",
                "Execution blocked because the maximum open position limit has been reached.");
        }

        if (riskPolicyEvaluator is not null &&
            request.TradingStrategyId.HasValue &&
            request.TradingStrategyVersionId.HasValue &&
            !string.IsNullOrWhiteSpace(request.Timeframe))
        {
            var riskEvaluation = await riskPolicyEvaluator.EvaluateAsync(
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
                    riskEvaluation);
            }
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
            return Allow();
        }

        if (overrideEntity.SessionDisabled)
        {
            return Block(
                "UserExecutionSessionDisabled",
                "Execution blocked because the user session is disabled by override.");
        }

        var allowedSymbols = ParseSymbols(overrideEntity.AllowedSymbolsCsv);

        if (allowedSymbols.Count > 0 && !allowedSymbols.Contains(normalizedSymbol))
        {
            return Block(
                "UserExecutionSymbolDenied",
                "Execution blocked because the symbol is outside the allow-list.");
        }

        var deniedSymbols = ParseSymbols(overrideEntity.DeniedSymbolsCsv);

        if (deniedSymbols.Contains(normalizedSymbol))
        {
            return Block(
                "UserExecutionSymbolDenied",
                "Execution blocked because the symbol is denied by override.");
        }

        if (overrideEntity.MaxOrderSize is decimal maxOrderSize &&
            (request.Quantity * request.Price) > maxOrderSize)
        {
            return Block(
                "UserExecutionMaxOrderSizeExceeded",
                "Execution blocked because the order notional exceeds the user override cap.");
        }

        if (overrideEntity.MaxDailyTrades is int maxDailyTrades &&
            maxDailyTrades >= 0 &&
            await ResolveTodayTradeCountAsync(normalizedUserId, cancellationToken) >= maxDailyTrades)
        {
            return Block(
                "UserExecutionMaxDailyTradesExceeded",
                "Execution blocked because the user daily trade cap has been reached.");
        }

        if (overrideEntity.ReduceOnly &&
            !isReduceOnlyOrder)
        {
            return Block(
                "UserExecutionReduceOnlyRequired",
                "Execution blocked because reduce-only mode is enabled for the user.");
        }

        return Allow();
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
        ExecutionOrderSide side,
        CancellationToken cancellationToken)
    {
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

    private static UserExecutionOverrideEvaluationResult Allow()
    {
        return new UserExecutionOverrideEvaluationResult(false, null, null);
    }

    private static UserExecutionOverrideEvaluationResult Block(
        string reason,
        string message,
        RiskVetoResult? riskEvaluation = null)
    {
        return new UserExecutionOverrideEvaluationResult(true, reason, message, riskEvaluation);
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

    private bool IsDevelopmentFuturesPilotOverrideAllowed(string? context)
    {
        return hostEnvironment?.IsDevelopment() == true &&
               TryReadBooleanFlag(context, "DevelopmentFuturesTestnetPilot");
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
