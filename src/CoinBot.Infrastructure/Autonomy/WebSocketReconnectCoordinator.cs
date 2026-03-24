using System.Threading;
using CoinBot.Application.Abstractions.Autonomy;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class WebSocketReconnectCoordinator(
    ILogger<WebSocketReconnectCoordinator> logger) : IWebSocketReconnectCoordinator
{
    private long generation;

    public long GetGeneration()
    {
        return Interlocked.Read(ref generation);
    }

    public Task RequestReconnectAsync(
        string reason,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nextGeneration = Interlocked.Increment(ref generation);

        logger.LogWarning(
            "WebSocket reconnect requested. Generation={Generation}, CorrelationId={CorrelationId}, Reason={Reason}.",
            nextGeneration,
            correlationId ?? "none",
            reason);

        return Task.CompletedTask;
    }
}
