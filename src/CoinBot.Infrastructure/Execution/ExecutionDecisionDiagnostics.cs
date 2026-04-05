using CoinBot.Application.Abstractions.Execution;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Execution;

internal static class ExecutionDecisionDiagnostics
{
    internal const string AllowedDecisionCode = "Allowed";

    internal static string ResolveDecisionOutcome(bool isBlocked)
    {
        return isBlocked ? "Block" : "Allow";
    }

    internal static string ResolveDecisionReasonCode(
        bool isBlocked,
        string? primaryCode,
        string? latencyReasonCode = null)
    {
        if (!isBlocked)
        {
            return AllowedDecisionCode;
        }

        var normalizedPrimaryCode = NormalizeOptional(primaryCode, toUpperInvariant: false);
        if (!string.IsNullOrWhiteSpace(normalizedPrimaryCode))
        {
            return normalizedPrimaryCode!;
        }

        var normalizedLatencyReason = NormalizeOptional(latencyReasonCode, toUpperInvariant: false);
        return string.IsNullOrWhiteSpace(normalizedLatencyReason)
            ? "Blocked"
            : normalizedLatencyReason!;
    }

    internal static string ResolveDecisionReasonType(
        string reasonCode,
        string? latencyReasonCode = null,
        string? riskOutcome = null,
        string? strategyDecisionOutcome = null)
    {
        if (string.Equals(reasonCode, AllowedDecisionCode, StringComparison.Ordinal))
        {
            return "Allow";
        }

        if (string.Equals(reasonCode, ExecutionGateBlockedReason.ContinuityGap.ToString(), StringComparison.Ordinal) ||
            IsContinuityGuardReason(reasonCode) ||
            IsContinuityGuardReason(latencyReasonCode))
        {
            return "ContinuityGap";
        }

        if (string.Equals(reasonCode, ExecutionGateBlockedReason.StaleMarketData.ToString(), StringComparison.Ordinal) ||
            string.Equals(reasonCode, ExecutionGateBlockedReason.MarketDataUnavailable.ToString(), StringComparison.Ordinal) ||
            string.Equals(reasonCode, ExecutionGateBlockedReason.ClockDriftExceeded.ToString(), StringComparison.Ordinal) ||
            string.Equals(reasonCode, ExecutionGateBlockedReason.DataLatencyGuardUnavailable.ToString(), StringComparison.Ordinal) ||
            string.Equals(latencyReasonCode, DegradedModeReasonCode.MarketDataLatencyBreached.ToString(), StringComparison.Ordinal) ||
            string.Equals(latencyReasonCode, DegradedModeReasonCode.MarketDataLatencyCritical.ToString(), StringComparison.Ordinal) ||
            string.Equals(latencyReasonCode, DegradedModeReasonCode.MarketDataUnavailable.ToString(), StringComparison.Ordinal) ||
            string.Equals(latencyReasonCode, DegradedModeReasonCode.ClockDriftExceeded.ToString(), StringComparison.Ordinal))
        {
            return "StaleData";
        }

        if (string.Equals(riskOutcome, "Vetoed", StringComparison.OrdinalIgnoreCase) ||
            reasonCode.StartsWith("UserExecutionRisk", StringComparison.Ordinal) ||
            string.Equals(reasonCode, "StrategyVetoed", StringComparison.Ordinal) ||
            string.Equals(strategyDecisionOutcome, "Vetoed", StringComparison.OrdinalIgnoreCase))
        {
            return "RiskVeto";
        }

        if (string.Equals(reasonCode, ExecutionGateBlockedReason.TradeMasterDisarmed.ToString(), StringComparison.Ordinal))
        {
            return "GlobalExecutionOff";
        }

        if (string.Equals(reasonCode, ExecutionGateBlockedReason.RequestedEnvironmentDoesNotMatchResolvedMode.ToString(), StringComparison.Ordinal) ||
            string.Equals(reasonCode, ExecutionGateBlockedReason.LiveExecutionBlockedByDemoMode.ToString(), StringComparison.Ordinal))
        {
            return "TradingModeMismatch";
        }

        if (string.Equals(reasonCode, ExecutionGateBlockedReason.SwitchConfigurationMissing.ToString(), StringComparison.Ordinal) ||
            reasonCode.Contains("Missing", StringComparison.OrdinalIgnoreCase) ||
            reasonCode.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
            reasonCode.Contains("Balance", StringComparison.OrdinalIgnoreCase) ||
            reasonCode.Contains("Config", StringComparison.OrdinalIgnoreCase) ||
            reasonCode.Contains("ExchangeAccount", StringComparison.OrdinalIgnoreCase) ||
            reasonCode.Contains("PrivatePlane", StringComparison.OrdinalIgnoreCase))
        {
            return "MissingPrivatePlaneOrConfig";
        }

        if (string.Equals(strategyDecisionOutcome, "SuppressedDuplicate", StringComparison.OrdinalIgnoreCase))
        {
            return "DuplicateSuppression";
        }

        if (string.Equals(strategyDecisionOutcome, "NotEvaluated", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(strategyDecisionOutcome, "NoSignalCandidate", StringComparison.OrdinalIgnoreCase))
        {
            return "StrategyCandidate";
        }

        return "Other";
    }

    internal static string ResolveDecisionSummary(
        bool isBlocked,
        string reasonType,
        string reasonCode,
        string? detail)
    {
        var humanSummary = ExtractHumanSummary(detail);
        if (!string.IsNullOrWhiteSpace(humanSummary))
        {
            return humanSummary!;
        }

        if (!isBlocked)
        {
            return "Execution decision allowed the request.";
        }

        return reasonType switch
        {
            "ContinuityGap" => "Continuity gap blocked execution.",
            "StaleData" => "Stale market data blocked execution.",
            "RiskVeto" => "Risk veto blocked execution.",
            "GlobalExecutionOff" => "Global execution switch blocked execution.",
            "TradingModeMismatch" => "Trading mode mismatch blocked execution.",
            "MissingPrivatePlaneOrConfig" => "Required private-plane, balance or config state is missing.",
            "DuplicateSuppression" => "Duplicate execution request was suppressed.",
            "StrategyCandidate" => "Strategy did not produce an executable candidate.",
            _ => $"Execution decision blocked the request ({reasonCode})."
        };
    }

    internal static string? ResolveStaleReason(string? latencyReasonCode)
    {
        return latencyReasonCode switch
        {
            nameof(DegradedModeReasonCode.MarketDataLatencyBreached) or nameof(DegradedModeReasonCode.MarketDataLatencyCritical) => "Market data stale",
            nameof(DegradedModeReasonCode.ClockDriftExceeded) => "Clock drift exceeded",
            nameof(DegradedModeReasonCode.MarketDataUnavailable) => "Market data unavailable",
            nameof(DegradedModeReasonCode.CandleDataGapDetected) => "Continuity gap detected",
            nameof(DegradedModeReasonCode.CandleDataDuplicateDetected) => "Duplicate candle detected",
            nameof(DegradedModeReasonCode.CandleDataOutOfOrderDetected) => "Out-of-order candle detected",
            _ => null
        };
    }

    internal static string? ResolveContinuityState(
        string? latencyReasonCode,
        int? continuityGapCount,
        DateTime? continuityRecoveredAtUtc)
    {
        if (continuityRecoveredAtUtc.HasValue)
        {
            return "Recovered after backfill";
        }

        if (IsContinuityGuardReason(latencyReasonCode))
        {
            return "Continuity guard active";
        }

        return continuityGapCount.GetValueOrDefault() > 0
            ? "Continuity gap detected"
            : "Continuity OK";
    }

    internal static DateTime? ExtractUtcToken(string key, params string?[] sources)
    {
        foreach (var source in sources)
        {
            var tokenValue = ExtractToken(key, source);
            if (tokenValue is null)
            {
                continue;
            }

            if (DateTime.TryParse(
                tokenValue,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsedValue))
            {
                return DateTime.SpecifyKind(parsedValue.ToUniversalTime(), DateTimeKind.Utc);
            }
        }

        return null;
    }

    internal static int? ExtractIntToken(string key, params string?[] sources)
    {
        foreach (var source in sources)
        {
            var tokenValue = ExtractToken(key, source);
            if (tokenValue is null)
            {
                continue;
            }

            if (int.TryParse(tokenValue, out var parsedValue))
            {
                return Math.Max(0, parsedValue);
            }
        }

        return null;
    }

    internal static string? ExtractToken(string key, params string?[] sources)
    {
        foreach (var source in sources)
        {
            var tokenValue = ExtractToken(key, source);
            if (!string.IsNullOrWhiteSpace(tokenValue))
            {
                return tokenValue;
            }
        }

        return null;
    }

    internal static int? ResolveDecisionDataAgeMilliseconds(
        DegradedModeState? degradedModeState,
        DateTime? decisionAtUtc,
        params string?[] sources)
    {
        var tokenValue = ExtractIntToken("DataAgeMs", sources);
        if (tokenValue.HasValue)
        {
            return tokenValue.Value;
        }

        if (degradedModeState?.LatestDataTimestampAtUtc is null || !decisionAtUtc.HasValue)
        {
            return null;
        }

        var deltaMilliseconds = (decisionAtUtc.Value.ToUniversalTime() - degradedModeState.LatestDataTimestampAtUtc.Value).TotalMilliseconds;
        if (deltaMilliseconds <= 0)
        {
            return 0;
        }

        return deltaMilliseconds >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Round(deltaMilliseconds, MidpointRounding.AwayFromZero);
    }

    internal static string? NormalizeOptional(string? value, bool toUpperInvariant)
    {
        var normalizedValue = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue) ||
            string.Equals(normalizedValue, "missing", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return toUpperInvariant
            ? normalizedValue.ToUpperInvariant()
            : normalizedValue;
    }

    internal static string? ExtractHumanSummary(string? detail)
    {
        var normalizedDetail = detail?.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrWhiteSpace(normalizedDetail))
        {
            return null;
        }

        var metadataStartIndex = normalizedDetail.IndexOf(" LatencyReason=", StringComparison.Ordinal);
        if (metadataStartIndex > 0)
        {
            normalizedDetail = normalizedDetail[..metadataStartIndex].TrimEnd();
        }

        var semicolonIndex = normalizedDetail.IndexOf(';');
        if (semicolonIndex > 0)
        {
            normalizedDetail = normalizedDetail[..semicolonIndex].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(normalizedDetail)
            ? null
            : normalizedDetail;
    }

    internal static bool IsContinuityGuardReason(string? reasonCode)
    {
        return string.Equals(reasonCode, nameof(DegradedModeReasonCode.CandleDataGapDetected), StringComparison.Ordinal) ||
            string.Equals(reasonCode, nameof(DegradedModeReasonCode.CandleDataDuplicateDetected), StringComparison.Ordinal) ||
            string.Equals(reasonCode, nameof(DegradedModeReasonCode.CandleDataOutOfOrderDetected), StringComparison.Ordinal);
    }

    private static string? ExtractToken(string key, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var token = key + "=";
        var tokenIndex = source.IndexOf(token, StringComparison.Ordinal);
        if (tokenIndex < 0)
        {
            return null;
        }

        var valueStartIndex = tokenIndex + token.Length;
        var valueEndIndex = source.IndexOf(';', valueStartIndex);
        var value = (valueEndIndex < 0
                ? source[valueStartIndex..]
                : source[valueStartIndex..valueEndIndex])
            .Trim()
            .TrimEnd('.');

        return string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "missing", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }
}
