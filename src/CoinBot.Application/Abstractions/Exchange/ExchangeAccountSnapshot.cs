namespace CoinBot.Application.Abstractions.Exchange;

public sealed record ExchangeAccountSnapshot(
    Guid ExchangeAccountId,
    string OwnerUserId,
    string ExchangeName,
    IReadOnlyCollection<ExchangeBalanceSnapshot> Balances,
    IReadOnlyCollection<ExchangePositionSnapshot> Positions,
    DateTime ObservedAtUtc,
    DateTime ReceivedAtUtc,
    string Source);
