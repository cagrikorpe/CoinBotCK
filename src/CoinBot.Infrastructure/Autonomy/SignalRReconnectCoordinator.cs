using CoinBot.Application.Abstractions.Autonomy;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Autonomy;

public sealed class SignalRReconnectCoordinator(
    ILogger<SignalRReconnectCoordinator> logger) : ISignalRReconnectCoordinator
{
    public Task RequestReconnectAsync(
        string reason,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogWarning(
            "SignalR reconnect requested. CorrelationId={CorrelationId}, Reason={Reason}.",
            correlationId ?? "none",
            reason);

        return Task.CompletedTask;
    }
}
