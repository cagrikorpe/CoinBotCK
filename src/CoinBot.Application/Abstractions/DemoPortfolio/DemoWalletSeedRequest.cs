using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.DemoPortfolio;

public sealed record DemoWalletSeedRequest(
    string OwnerUserId,
    ExecutionEnvironment Environment,
    string OperationId,
    string Asset,
    decimal Amount,
    DateTime? OccurredAtUtc = null);
