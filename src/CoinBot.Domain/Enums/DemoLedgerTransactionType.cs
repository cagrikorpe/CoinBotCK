namespace CoinBot.Domain.Enums;

public enum DemoLedgerTransactionType
{
    WalletSeeded = 0,
    FundsReserved = 1,
    FundsReleased = 2,
    FillApplied = 3,
    MarkPriceUpdated = 4,
    Liquidated = 5,
    SessionBootstrapped = 6,
    Reconciled = 7
}
