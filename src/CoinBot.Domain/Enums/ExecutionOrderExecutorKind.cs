namespace CoinBot.Domain.Enums;

public enum ExecutionOrderExecutorKind
{
    Unknown = 0,
    Virtual = 1,
    Binance = 2,
    BinanceTestnet = 3,
    FuturesTestnet = BinanceTestnet
}
