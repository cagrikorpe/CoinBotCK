namespace CoinBot.Application.Abstractions.Autonomy;

public interface ISignalRReconnectCoordinator
{
    Task RequestReconnectAsync(
        string reason,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}
