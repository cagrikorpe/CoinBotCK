namespace CoinBot.Domain.Enums;

public enum ExecutionEnvironment
{
    Demo = 0,
    Paper = Demo,
    Live = 1,
    BinanceTestnet = 2,
    Testnet = BinanceTestnet
}
