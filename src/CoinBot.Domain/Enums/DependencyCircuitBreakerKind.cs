namespace CoinBot.Domain.Enums;

public enum DependencyCircuitBreakerKind
{
    WebSocket = 0,
    RestMarketData = 1,
    OrderExecution = 2,
    AccountValidation = 3
}
