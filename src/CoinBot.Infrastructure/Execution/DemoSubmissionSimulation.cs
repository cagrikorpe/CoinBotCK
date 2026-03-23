namespace CoinBot.Infrastructure.Execution;

public sealed record DemoSubmissionSimulation(
    DemoFillReservationPlan? Reservation,
    DemoFillSimulation? Fill);
