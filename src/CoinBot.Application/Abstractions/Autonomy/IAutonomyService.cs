namespace CoinBot.Application.Abstractions.Autonomy;

public interface IAutonomyService
{
    Task<PreFlightSimulationResult> SimulateAsync(
        PreFlightSimulationRequest request,
        CancellationToken cancellationToken = default);

    Task<AutonomyDecisionResult> EvaluateAsync(
        AutonomyDecisionRequest request,
        CancellationToken cancellationToken = default);
}
