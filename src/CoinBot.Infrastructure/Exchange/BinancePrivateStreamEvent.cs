using CoinBot.Application.Abstractions.Exchange;

namespace CoinBot.Infrastructure.Exchange;

public sealed record BinancePrivateStreamEvent(
    string EventType,
    DateTime EventTimeUtc,
    IReadOnlyCollection<ExchangeBalanceSnapshot> BalanceUpdates,
    IReadOnlyCollection<ExchangePositionSnapshot> PositionUpdates);
