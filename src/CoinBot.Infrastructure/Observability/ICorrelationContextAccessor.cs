namespace CoinBot.Infrastructure.Observability;

public interface ICorrelationContextAccessor
{
    CorrelationContext? Current { get; }

    IDisposable BeginScope(CorrelationContext correlationContext);
}
