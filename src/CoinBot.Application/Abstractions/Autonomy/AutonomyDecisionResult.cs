namespace CoinBot.Application.Abstractions.Autonomy;

public sealed record AutonomyDecisionResult(
    PreFlightSimulationResult Simulation,
    bool AutoExecuted,
    bool ReviewQueued,
    string? ApprovalId,
    string Outcome,
    string? Detail);
