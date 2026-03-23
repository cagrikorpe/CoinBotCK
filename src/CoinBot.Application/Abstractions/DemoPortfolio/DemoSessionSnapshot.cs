using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.DemoPortfolio;

public sealed record DemoSessionSnapshot(
    Guid SessionId,
    int SequenceNumber,
    string SeedAsset,
    decimal SeedAmount,
    DemoSessionState State,
    DemoConsistencyStatus ConsistencyStatus,
    DateTime StartedAtUtc,
    DateTime? ClosedAtUtc,
    DateTime? LastConsistencyCheckedAtUtc,
    DateTime? LastDriftDetectedAtUtc,
    string? LastDriftSummary);
