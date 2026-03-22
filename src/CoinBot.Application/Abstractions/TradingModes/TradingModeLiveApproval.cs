namespace CoinBot.Application.Abstractions.Execution;

public sealed record TradingModeLiveApproval(
    string ApprovalReference,
    DateTime? ApprovedAtUtc = null,
    bool ConfirmedDemoDataIsolation = false);
