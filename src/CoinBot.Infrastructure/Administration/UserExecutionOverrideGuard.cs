using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Execution;

public sealed class UserExecutionOverrideGuard(
    ApplicationDbContext dbContext,
    ITradingModeResolver tradingModeResolver,
    IGlobalPolicyEngine? globalPolicyEngine = null,
    ILogger<UserExecutionOverrideGuard>? logger = null) : IUserExecutionOverrideGuard
{
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

            if (resolution.EffectiveMode != ExecutionEnvironment.Live)
            {
                return Block(
                    "LiveProviderBlockedByResolvedDemoMode",
                    "Live provider access blocked because the effective mode resolved to Demo.");
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
            !await IsReduceOnlyOrderAsync(normalizedUserId, normalizedSymbol, request.Environment, request.Side, cancellationToken))
        {
            return Block(
                "UserExecutionReduceOnlyRequired",
                "Execution blocked because reduce-only mode is enabled for the user.");
        }

        return Allow();
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

    private static UserExecutionOverrideEvaluationResult Block(string reason, string message)
    {
        return new UserExecutionOverrideEvaluationResult(true, reason, message);
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
}
