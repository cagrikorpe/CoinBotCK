namespace CoinBot.Application.Abstractions.Exchange;

public sealed record ExchangeBalanceSnapshot(
    string Asset,
    decimal WalletBalance,
    decimal CrossWalletBalance,
    decimal? AvailableBalance,
    decimal? MaxWithdrawAmount,
    DateTime ExchangeUpdatedAtUtc);
