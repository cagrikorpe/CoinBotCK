using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.DemoPortfolio;

public sealed record DemoFundsReleaseRequest(
    string OwnerUserId,
    ExecutionEnvironment Environment,
    string OperationId,
    string Asset,
    decimal Amount,
    string? OrderId = null,
    DateTime? OccurredAtUtc = null);
