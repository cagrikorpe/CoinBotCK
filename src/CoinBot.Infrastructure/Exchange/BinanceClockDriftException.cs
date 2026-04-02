namespace CoinBot.Infrastructure.Exchange;

public sealed class BinanceClockDriftException : InvalidOperationException
{
    public BinanceClockDriftException(string message)
        : base(message)
    {
    }
}
