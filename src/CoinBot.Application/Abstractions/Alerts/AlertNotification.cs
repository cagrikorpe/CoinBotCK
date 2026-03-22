namespace CoinBot.Application.Abstractions.Alerts;

public sealed record AlertNotification(
    string Code,
    AlertSeverity Severity,
    string Title,
    string Message,
    string? CorrelationId = null);
