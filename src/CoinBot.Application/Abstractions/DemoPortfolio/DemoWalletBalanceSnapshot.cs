namespace CoinBot.Application.Abstractions.DemoPortfolio;

public sealed record DemoWalletBalanceSnapshot(
    string Asset,
    decimal AvailableBalance,
    decimal ReservedBalance)
{
    public decimal TotalBalance => AvailableBalance + ReservedBalance;
}
