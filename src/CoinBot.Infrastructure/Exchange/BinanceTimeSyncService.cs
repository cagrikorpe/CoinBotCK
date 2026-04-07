using System.Diagnostics;
using System.Text.Json;
using CoinBot.Application.Abstractions.Exchange;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Exchange;

public sealed class BinanceTimeSyncService(
    HttpClient httpClient,
    IMemoryCache memoryCache,
    TimeProvider timeProvider,
    IOptions<BinancePrivateDataOptions> options,
    ILogger<BinanceTimeSyncService> logger) : IBinanceTimeSyncService
{
    private const string CacheKey = "binance-server-time-sync";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly BinancePrivateDataOptions optionsValue = options.Value;

    public async Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var localAppTimeUtc = timeProvider.GetUtcNow().UtcDateTime;

        if (!forceRefresh &&
            memoryCache.TryGetValue<CachedTimeSyncState>(CacheKey, out var cachedState) &&
            cachedState is not null)
        {
            return BuildSnapshot(cachedState, localAppTimeUtc, "Cached", null);
        }

        var refreshedState = await RefreshAsync(cancellationToken);

        if (refreshedState is not null)
        {
            return BuildSnapshot(refreshedState, localAppTimeUtc, "Synchronized", null);
        }

        if (memoryCache.TryGetValue<CachedTimeSyncState>(CacheKey, out cachedState) &&
            cachedState is not null)
        {
            return BuildSnapshot(
                cachedState,
                localAppTimeUtc,
                "CachedFallback",
                "Binance server time probe yenilenemedi; son başarılı offset kullanılmaya devam ediyor.");
        }

        return new BinanceTimeSyncSnapshot(
            localAppTimeUtc,
            ExchangeServerTimeUtc: null,
            OffsetMilliseconds: 0,
            RoundTripMilliseconds: null,
            LastSynchronizedAtUtc: null,
            StatusCode: "Unavailable",
            FailureReason: "Binance server time probe başarısız olduğu için offset üretilemedi.");
    }

    public async Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken: cancellationToken);

        if (!snapshot.HasSynchronizedOffset)
        {
            snapshot = await GetSnapshotAsync(forceRefresh: true, cancellationToken);
        }

        if (!snapshot.HasSynchronizedOffset)
        {
            var failureReason = string.IsNullOrWhiteSpace(snapshot.FailureReason)
                ? "Binance server time offset could not be synchronized."
                : snapshot.FailureReason;
            var lastSyncText = snapshot.LastSynchronizedAtUtc?.ToString("O") ?? "missing";
            throw new BinanceClockDriftException(
                $"Binance request timestamp is unavailable because server-time offset could not be synchronized. Status={snapshot.StatusCode}; OffsetMs={snapshot.OffsetMilliseconds}; LastSyncUtc={lastSyncText}; Reason={failureReason}");
        }

        return timeProvider.GetUtcNow().ToUnixTimeMilliseconds() + snapshot.OffsetMilliseconds;
    }

    private async Task<CachedTimeSyncState?> RefreshAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ProbeTimeout);
        var stopwatch = Stopwatch.StartNew();
        var probeStartedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            using var response = await httpClient.GetAsync("fapi/v1/time", timeoutCts.Token);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("serverTime", out var serverTimeElement) ||
                !serverTimeElement.TryGetInt64(out var serverTimeMilliseconds) ||
                serverTimeMilliseconds <= 0)
            {
                throw new InvalidOperationException("Binance server time response did not contain a valid serverTime.");
            }

            var probeCompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            var exchangeServerTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(serverTimeMilliseconds).UtcDateTime;
            var localMidpointUtc = probeStartedAtUtc.AddTicks((probeCompletedAtUtc - probeStartedAtUtc).Ticks / 2);
            var offsetMilliseconds = ToOffsetMilliseconds(exchangeServerTimeUtc - localMidpointUtc);
            var state = new CachedTimeSyncState(
                offsetMilliseconds,
                RoundTripMilliseconds: ToRoundTripMilliseconds(stopwatch.ElapsedMilliseconds),
                LastSynchronizedAtUtc: probeCompletedAtUtc);

            var entryOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(optionsValue.ServerTimeSyncRefreshSeconds)
            };
            entryOptions.SetSize(1);
            memoryCache.Set(CacheKey, state, entryOptions);

            return state;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Binance server time probe failed.");
            return null;
        }
    }

    private static BinanceTimeSyncSnapshot BuildSnapshot(
        CachedTimeSyncState state,
        DateTime localAppTimeUtc,
        string statusCode,
        string? failureReason)
    {
        return new BinanceTimeSyncSnapshot(
            localAppTimeUtc,
            ExchangeServerTimeUtc: localAppTimeUtc.AddMilliseconds(state.OffsetMilliseconds),
            OffsetMilliseconds: state.OffsetMilliseconds,
            RoundTripMilliseconds: state.RoundTripMilliseconds,
            LastSynchronizedAtUtc: state.LastSynchronizedAtUtc,
            StatusCode: statusCode,
            FailureReason: failureReason);
    }

    private static long ToOffsetMilliseconds(TimeSpan offset)
    {
        var rounded = Math.Round(offset.TotalMilliseconds, MidpointRounding.AwayFromZero);

        if (rounded >= long.MaxValue)
        {
            return long.MaxValue;
        }

        if (rounded <= long.MinValue)
        {
            return long.MinValue;
        }

        return (long)rounded;
    }

    private static int ToRoundTripMilliseconds(long elapsedMilliseconds)
    {
        if (elapsedMilliseconds <= 0)
        {
            return 0;
        }

        return elapsedMilliseconds >= int.MaxValue
            ? int.MaxValue
            : (int)elapsedMilliseconds;
    }

    private sealed record CachedTimeSyncState(
        long OffsetMilliseconds,
        int? RoundTripMilliseconds,
        DateTime LastSynchronizedAtUtc);
}

