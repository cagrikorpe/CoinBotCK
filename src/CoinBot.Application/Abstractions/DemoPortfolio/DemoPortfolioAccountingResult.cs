namespace CoinBot.Application.Abstractions.DemoPortfolio;

public sealed record DemoPortfolioAccountingResult(
    DemoLedgerTransactionSnapshot Transaction,
    DemoPositionSnapshot? Position,
    IReadOnlyCollection<DemoWalletBalanceSnapshot> Wallets,
    bool IsReplay);
