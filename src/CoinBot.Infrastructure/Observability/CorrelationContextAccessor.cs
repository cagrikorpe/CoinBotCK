using System.Threading;

namespace CoinBot.Infrastructure.Observability;

public sealed class CorrelationContextAccessor : ICorrelationContextAccessor
{
    private static readonly AsyncLocal<CorrelationContextHolder?> CurrentContext = new();

    public CorrelationContext? Current => CurrentContext.Value?.CorrelationContext;

    public IDisposable BeginScope(CorrelationContext correlationContext)
    {
        ArgumentNullException.ThrowIfNull(correlationContext);

        var previousContext = CurrentContext.Value;
        CurrentContext.Value = new CorrelationContextHolder(correlationContext);

        return new RevertScope(() => CurrentContext.Value = previousContext);
    }

    private sealed record CorrelationContextHolder(CorrelationContext CorrelationContext);

    private sealed class RevertScope(Action onDispose) : IDisposable
    {
        private Action? onDispose = onDispose;

        public void Dispose()
        {
            onDispose?.Invoke();
            onDispose = null;
        }
    }
}
