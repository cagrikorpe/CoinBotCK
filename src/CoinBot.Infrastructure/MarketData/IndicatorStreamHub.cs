using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CoinBot.Application.Abstractions.Indicators;

namespace CoinBot.Infrastructure.MarketData;

public sealed class IndicatorStreamHub
{
    private readonly ConcurrentDictionary<long, Subscription> subscriptions = new();
    private long nextSubscriptionId;

    public void Publish(StrategyIndicatorSnapshot snapshot)
    {
        foreach (var subscription in subscriptions.Values)
        {
            if (!subscription.Accepts(snapshot.Symbol, snapshot.Timeframe))
            {
                continue;
            }

            subscription.Channel.Writer.TryWrite(snapshot);
        }
    }

    public async IAsyncEnumerable<StrategyIndicatorSnapshot> SubscribeAsync(
        IReadOnlyCollection<IndicatorSubscription> indicatorSubscriptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filter = indicatorSubscriptions.Count == 0
            ? null
            : new HashSet<string>(
                indicatorSubscriptions.Select(subscription => CreateKey(subscription.Symbol, subscription.Timeframe)),
                StringComparer.Ordinal);
        var channel = Channel.CreateUnbounded<StrategyIndicatorSnapshot>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });
        var subscriptionId = Interlocked.Increment(ref nextSubscriptionId);

        subscriptions[subscriptionId] = new Subscription(filter, channel);

        try
        {
            await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return snapshot;
            }
        }
        finally
        {
            subscriptions.TryRemove(subscriptionId, out _);
            channel.Writer.TryComplete();
        }
    }

    private static string CreateKey(string symbol, string timeframe)
    {
        return $"{symbol}:{timeframe}";
    }

    private sealed class Subscription(HashSet<string>? keys, Channel<StrategyIndicatorSnapshot> channel)
    {
        public Channel<StrategyIndicatorSnapshot> Channel { get; } = channel;

        public bool Accepts(string symbol, string timeframe)
        {
            return keys is null || keys.Contains(CreateKey(symbol, timeframe));
        }
    }
}
