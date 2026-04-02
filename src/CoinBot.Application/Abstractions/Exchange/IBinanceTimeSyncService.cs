namespace CoinBot.Application.Abstractions.Exchange;

public interface IBinanceTimeSyncService
{
    Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default);
}

public sealed record BinanceTimeSyncSnapshot(
    DateTime LocalAppTimeUtc,
    DateTime? ExchangeServerTimeUtc,
    long OffsetMilliseconds,
    int? RoundTripMilliseconds,
    DateTime? LastSynchronizedAtUtc,
    string StatusCode,
    string? FailureReason)
{
    public int? ClockDriftMilliseconds =>
        ExchangeServerTimeUtc is null
            ? null
            : ToClockDriftMilliseconds(ExchangeServerTimeUtc.Value, LocalAppTimeUtc);

    public bool HasSynchronizedOffset =>
        ExchangeServerTimeUtc.HasValue &&
        LastSynchronizedAtUtc.HasValue;

    private static int ToClockDriftMilliseconds(DateTime exchangeServerTimeUtc, DateTime localAppTimeUtc)
    {
        var driftMilliseconds = Math.Abs((exchangeServerTimeUtc - localAppTimeUtc).TotalMilliseconds);

        if (driftMilliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)Math.Round(driftMilliseconds, MidpointRounding.AwayFromZero);
    }
}
