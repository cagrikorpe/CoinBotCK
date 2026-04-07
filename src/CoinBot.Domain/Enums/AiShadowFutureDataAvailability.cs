namespace CoinBot.Domain.Enums;

public enum AiShadowFutureDataAvailability
{
    Available = 1,
    MissingFutureCandle = 2,
    MissingReferenceCandle = 3,
    InvalidReferencePrice = 4
}
