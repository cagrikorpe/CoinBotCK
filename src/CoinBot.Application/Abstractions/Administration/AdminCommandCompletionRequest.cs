using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Administration;

public sealed record AdminCommandCompletionRequest(
    string CommandId,
    string PayloadHash,
    AdminCommandStatus Status,
    string? ResultSummary,
    string? CorrelationId);
