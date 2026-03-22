namespace CoinBot.UnitTests.Infrastructure.Mfa;

internal sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset currentUtcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => currentUtcNow;

    public void Advance(TimeSpan timeSpan)
    {
        currentUtcNow = currentUtcNow.Add(timeSpan);
    }
}
