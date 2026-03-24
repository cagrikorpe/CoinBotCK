namespace CoinBot.Application.Abstractions.Autonomy;

public interface IWebSocketReconnectCoordinator
{
    long GetGeneration();

    Task RequestReconnectAsync(
        string reason,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}
