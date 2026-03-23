namespace CoinBot.Infrastructure.Execution;

public sealed record DemoFillReservationPlan(
    string Asset,
    decimal Amount,
    decimal ConsumedAmount,
    string Reason);
