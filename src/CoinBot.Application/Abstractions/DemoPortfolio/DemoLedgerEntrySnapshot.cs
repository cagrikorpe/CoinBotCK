namespace CoinBot.Application.Abstractions.DemoPortfolio;

public sealed record DemoLedgerEntrySnapshot(
    string Asset,
    decimal AvailableDelta,
    decimal ReservedDelta,
    decimal AvailableBalanceAfter,
    decimal ReservedBalanceAfter);
