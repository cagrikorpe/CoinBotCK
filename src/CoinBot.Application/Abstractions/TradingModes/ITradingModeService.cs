using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public interface ITradingModeService
{
    Task<TradingModeResolution> SetUserTradingModeOverrideAsync(
        string userId,
        ExecutionEnvironment? modeOverride,
        string actor,
        TradingModeLiveApproval? liveApproval = null,
        string? context = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default);

    Task<TradingModeResolution> SetBotTradingModeOverrideAsync(
        Guid botId,
        ExecutionEnvironment? modeOverride,
        string actor,
        TradingModeLiveApproval? liveApproval = null,
        string? context = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default);

    Task<TradingStrategyPromotionResult> PublishStrategyAsync(
        Guid strategyId,
        ExecutionEnvironment targetMode,
        string actor,
        TradingModeLiveApproval? liveApproval = null,
        string? context = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}
