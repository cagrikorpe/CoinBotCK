using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CoinBot.Infrastructure.Dashboard;

public sealed record UserOperationsUpdate(
    string OwnerUserId,
    string EventType,
    Guid? BotId,
    Guid? ExecutionOrderId,
    string? State,
    string? FailureCode,
    DateTime OccurredAtUtc);

public sealed class UserOperationsStreamHub
{
    private readonly ConcurrentDictionary<long, Channel<UserOperationsUpdate>> subscriptions = new();
    private long nextSubscriptionId;

    public void Publish(UserOperationsUpdate update)
    {
        foreach (var subscription in subscriptions.Values)
        {
            subscription.Writer.TryWrite(update);
        }
    }

    public async IAsyncEnumerable<UserOperationsUpdate> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<UserOperationsUpdate>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });
        var subscriptionId = Interlocked.Increment(ref nextSubscriptionId);
        subscriptions[subscriptionId] = channel;

        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            subscriptions.TryRemove(subscriptionId, out _);
            channel.Writer.TryComplete();
        }
    }
}
