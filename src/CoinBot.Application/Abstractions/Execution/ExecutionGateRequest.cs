using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Execution;

public sealed record ExecutionGateRequest(
    string Actor,
    string Action,
    string Target,
    ExecutionEnvironment Environment,
    string? Context = null,
    string? CorrelationId = null,
    string? UserId = null,
    Guid? BotId = null,
    string? StrategyKey = null);
