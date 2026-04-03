using System.Security.Cryptography;
using System.Text;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Execution;

public static class DegradedModeDefaults
{
    public static readonly Guid SingletonId = Guid.Parse("3e17e8ef-3a73-45cc-8c32-a11fa55178d7");

    public static Guid ResolveStateId(string? symbol, string? timeframe)
    {
        if (string.IsNullOrWhiteSpace(symbol) ||
            string.IsNullOrWhiteSpace(timeframe))
        {
            return SingletonId;
        }

        var payload = $"degraded-mode:{symbol.Trim().ToUpperInvariant()}:{timeframe.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        return new Guid(hash[..16]);
    }

    public static DegradedModeState CreateEntity(
        DateTime createdAtUtc,
        string? symbol = null,
        string? timeframe = null)
    {
        return new DegradedModeState
        {
            Id = ResolveStateId(symbol, timeframe),
            StateCode = DegradedModeStateCode.Stopped,
            ReasonCode = DegradedModeReasonCode.MarketDataUnavailable,
            SignalFlowBlocked = true,
            ExecutionFlowBlocked = true,
            LastStateChangedAtUtc = createdAtUtc,
            LatestSymbol = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim().ToUpperInvariant(),
            LatestTimeframe = string.IsNullOrWhiteSpace(timeframe) ? null : timeframe.Trim()
        };
    }
}