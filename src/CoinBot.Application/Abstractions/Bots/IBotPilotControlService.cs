namespace CoinBot.Application.Abstractions.Bots;

public interface IBotPilotControlService
{
    Task<BotPilotToggleResult> SetEnabledAsync(
        string ownerUserId,
        Guid botId,
        bool isEnabled,
        string actor,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}
