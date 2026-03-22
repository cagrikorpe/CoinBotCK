using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CoinBot.Application.Abstractions.Exchange;

namespace CoinBot.Infrastructure.Exchange;

public sealed class ExchangeAccountSnapshotHub
{
    private readonly ConcurrentDictionary<long, Channel<ExchangeAccountSnapshot>> subscriptions = new();
    private long nextSubscriptionId;

    public void Publish(ExchangeAccountSnapshot snapshot)
    {
        foreach (var channel in subscriptions.Values)
        {
            channel.Writer.TryWrite(snapshot);
        }
    }

    public async IAsyncEnumerable<ExchangeAccountSnapshot> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ExchangeAccountSnapshot>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });
        var subscriptionId = Interlocked.Increment(ref nextSubscriptionId);

        subscriptions[subscriptionId] = channel;

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
}
