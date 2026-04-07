namespace CoinBot.Domain.Enums;

public enum FeatureSnapshotQualityReason
{
    None = 0,
    InsufficientCandles = 1,
    MissingInputs = 2,
    InvalidNumericValue = 3,
    InvalidRange = 4,
    IncompleteSnapshot = 5
}
