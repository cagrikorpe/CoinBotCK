using CoinBot.Infrastructure.Exchange;

namespace CoinBot.Infrastructure.Execution;

public sealed record ExecutionTargetDispatchResult(
    string ExternalOrderId,
    DateTime SubmittedAtUtc,
    string? Detail,
    BinanceOrderStatusSnapshot? InitialSnapshot = null);
