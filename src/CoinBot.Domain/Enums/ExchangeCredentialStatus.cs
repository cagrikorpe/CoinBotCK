namespace CoinBot.Domain.Enums;

public enum ExchangeCredentialStatus
{
    Missing = 0,
    PendingValidation = 1,
    Active = 2,
    RevalidationRequired = 3,
    RotationRequired = 4,
    Invalid = 5
}
