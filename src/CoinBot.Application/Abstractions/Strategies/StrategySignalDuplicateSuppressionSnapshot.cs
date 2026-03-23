namespace CoinBot.Application.Abstractions.Strategies;

public sealed record StrategySignalDuplicateSuppressionSnapshot(
    bool Enabled,
    bool WasSuppressed,
    string Fingerprint);
