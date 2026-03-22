namespace CoinBot.Application.Abstractions.DataScope;

public interface IDataScopeContext
{
    string? UserId { get; }

    bool HasIsolationBypass { get; }
}
