using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.DemoPortfolio;

public sealed record DemoSessionResetRequest(
    string OwnerUserId,
    ExecutionEnvironment Environment,
    string Actor,
    string? Reason = null,
    string? CorrelationId = null,
    string? SeedAsset = null,
    decimal? SeedAmount = null);
