namespace CoinBot.Application.Abstractions.DataScope;

public interface IDataScopeContextAccessor : IDataScopeContext
{
    IDisposable BeginScope(string? userId = null, bool hasIsolationBypass = false);
}
