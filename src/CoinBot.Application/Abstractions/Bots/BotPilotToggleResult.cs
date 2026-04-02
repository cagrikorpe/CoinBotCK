namespace CoinBot.Application.Abstractions.Bots;

public sealed record BotPilotToggleResult(
    Guid BotId,
    bool IsEnabled,
    bool IsSuccessful,
    string? FailureCode,
    string? FailureReason);
