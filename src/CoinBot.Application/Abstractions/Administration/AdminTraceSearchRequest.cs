namespace CoinBot.Application.Abstractions.Administration;

public sealed record AdminTraceSearchRequest(
    string? Query = null,
    string? CorrelationId = null,
    string? DecisionId = null,
    string? ExecutionAttemptId = null,
    string? UserId = null,
    int Take = 50);
