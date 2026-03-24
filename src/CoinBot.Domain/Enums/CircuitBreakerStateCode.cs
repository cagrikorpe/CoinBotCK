namespace CoinBot.Domain.Enums;

public enum CircuitBreakerStateCode
{
    Closed = 0,
    Retrying = 1,
    Cooldown = 2,
    HalfOpen = 3,
    Degraded = 4
}
