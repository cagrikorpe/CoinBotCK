namespace CoinBot.Domain.Enums;

public enum FeatureSnapshotState
{
    WarmingUp = 1,
    Ready = 2,
    Stale = 3,
    MissingData = 4,
    Invalid = 5
}

