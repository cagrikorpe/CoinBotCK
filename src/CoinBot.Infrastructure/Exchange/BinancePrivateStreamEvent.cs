using CoinBot.Domain.Enums;
using CoinBot.Application.Abstractions.Exchange;

namespace CoinBot.Infrastructure.Exchange;

public sealed record BinancePrivateStreamEvent(
    string EventType,
    DateTime EventTimeUtc,
    IReadOnlyCollection<ExchangeBalanceSnapshot> BalanceUpdates,
    IReadOnlyCollection<ExchangePositionSnapshot> PositionUpdates,
    IReadOnlyCollection<BinanceOrderStatusSnapshot> OrderUpdates,
    bool RequiresAccountRefresh = false,
    ExchangeDataPlane Plane = ExchangeDataPlane.Futures);
