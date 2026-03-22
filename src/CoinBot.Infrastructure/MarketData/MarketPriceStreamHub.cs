using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CoinBot.Application.Abstractions.MarketData;

namespace CoinBot.Infrastructure.MarketData;

public sealed class MarketPriceStreamHub
{
    private readonly ConcurrentDictionary<long, Subscription> subscriptions = new();
    private long nextSubscriptionId;

    public void Publish(MarketPriceSnapshot snapshot)
    {
        foreach (var subscription in subscriptions.Values)
        {
            if (!subscription.Accepts(snapshot.Symbol))
            {
                continue;
            }

            subscription.Channel.Writer.TryWrite(snapshot);
        }
    }

    public async IAsyncEnumerable<MarketPriceSnapshot> SubscribeAsync(
        IReadOnlyCollection<string> symbols,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filter = symbols.Count == 0
            ? null
            : new HashSet<string>(symbols, StringComparer.Ordinal);
        var channel = Channel.CreateUnbounded<MarketPriceSnapshot>(new UnboundedChannelOptions
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

    private sealed class Subscription(HashSet<string>? symbols, Channel<MarketPriceSnapshot> channel)
    {
        public Channel<MarketPriceSnapshot> Channel { get; } = channel;

        public bool Accepts(string symbol)
        {
            return symbols is null || symbols.Contains(symbol);
        }
    }
}
