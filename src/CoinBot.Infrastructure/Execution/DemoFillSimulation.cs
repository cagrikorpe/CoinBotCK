namespace CoinBot.Infrastructure.Execution;

public sealed record DemoFillSimulation(
    decimal ReferencePrice,
    string ReferenceSource,
    DateTime ObservedAtUtc,
    decimal FillQuantity,
    decimal FillPrice,
    string FeeAsset,
    decimal FeeRate,
    decimal FeeAmount,
    decimal FeeAmountInQuote,
    bool IsFinalFill,
    string EventCode,
    string Detail);
