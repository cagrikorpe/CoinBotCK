using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BotExecutionPilotOptions
{
    public bool Enabled { get; set; }

    public bool PilotActivationEnabled { get; set; } = false;

    public bool AllowNonDevelopmentHost { get; set; } = false;

    public bool AllowGlobalSwitchBypass { get; set; } = false;

    public ExecutionEnvironment SignalEvaluationMode { get; set; } = ExecutionEnvironment.Live;

    public ExecutionEnvironment ExecutionDispatchMode { get; set; } = ExecutionEnvironment.Live;

    [Required]
    public string DefaultSymbol { get; set; } = "BTCUSDT";

    [Required]
    public string Timeframe { get; set; } = "1m";

    [Range(1, 125)]
    public decimal DefaultLeverage { get; set; } = 1m;

    [Range(1, 125)]
    public decimal MaxAllowedLeverage { get; set; } = 1m;

    public bool AllowNonOneLeverageForClockDriftSmoke { get; set; } = false;

    [Required]
    public string DefaultMarginType { get; set; } = "ISOLATED";

    public string[] AllowedSymbols { get; set; } = [];

    public string[]? AllowedExecutionSymbols { get; set; }

    public string[] AllowedUserIds { get; set; } = [];

    public string[] AllowedBotIds { get; set; } = [];

    public string? MaxPilotOrderNotional { get; set; }

    public decimal? MaxOrderNotional
    {
        get => TryResolveMaxPilotOrderNotional(out var value) ? value : null;
        set => MaxPilotOrderNotional = value?.ToString("0.############################", CultureInfo.InvariantCulture);
    }

    [Range(1, 2)]
    public decimal MinNotionalSafetyMultiplier { get; set; } = 1.02m;

    public bool EnableRuntimeExitQuality { get; set; } = true;

    public bool AutoManageAdoptedPositions { get; set; } = false;

    public bool EnableEntryHysteresis { get; set; } = true;

    public bool EnableRegimeAwareEntryDiscipline { get; set; } = true;

    public bool? LongRegimeFilterEnabled { get; set; }

    public bool? ShortRegimeFilterEnabled { get; set; }

    [Range(0, 100)]
    public decimal TakeProfitPercentage { get; set; } = 0.60m;

    [Range(0, 100)]
    public decimal MinTakeProfitPct { get; set; } = 0m;

    public bool ExitOnReverseSignalOnlyIfProfitable { get; set; } = true;

    public bool AllowStopLossExit { get; set; } = true;

    public bool AllowRiskExit { get; set; } = true;

    [Range(0, 100)]
    public decimal StopLossPercentage { get; set; } = 0.45m;

    [Range(0, 100)]
    public decimal TrailingStopActivationPercentage { get; set; } = 0.50m;

    [Range(0, 100)]
    public decimal TrailingStopPercentage { get; set; } = 0.25m;

    [Range(0, 100)]
    public decimal BreakEvenActivationPercentage { get; set; } = 0.35m;

    [Range(0, 100)]
    public decimal BreakEvenBufferPercentage { get; set; } = 0.05m;

    [Range(0, 120)]
    public int EntryHysteresisCooldownMinutes { get; set; } = 5;

    [Range(0, 100)]
    public decimal EntryHysteresisReentryBufferPercentage { get; set; } = 0.20m;

    [Range(0, 120)]
    public int? LongEntryHysteresisCooldownMinutes { get; set; }

    [Range(0, 120)]
    public int? ShortEntryHysteresisCooldownMinutes { get; set; }

    [Range(0, 100)]
    public decimal? LongEntryHysteresisReentryBufferPercentage { get; set; }

    [Range(0, 100)]
    public decimal? ShortEntryHysteresisReentryBufferPercentage { get; set; }

    [Range(0, 100)]
    public decimal RegimeMaxEntryRsi { get; set; } = 68m;

    [Range(-100, 100)]
    public decimal RegimeMinMacdHistogram { get; set; } = 0m;

    [Range(0, 100)]
    public decimal RegimeMinBollingerWidthPercentage { get; set; } = 0.20m;

    [Range(0, 100)]
    public decimal RegimeMinPriceAboveMiddleBandPercentage { get; set; } = 0m;

    [Range(0, 100)]
    public decimal? LongRegimeMaxEntryRsi { get; set; }

    [Range(-100, 100)]
    public decimal? LongRegimeMinMacdHistogram { get; set; }

    [Range(0, 100)]
    public decimal? LongRegimeMinBollingerWidthPercentage { get; set; }

    [Range(0, 100)]
    public decimal? LongRegimeMinPriceAboveMiddleBandPercentage { get; set; }

    [Range(0, 100)]
    public decimal? ShortRegimeMinEntryRsi { get; set; }

    [Range(-100, 100)]
    public decimal? ShortRegimeMaxMacdHistogram { get; set; }

    [Range(0, 100)]
    public decimal? ShortRegimeMinBollingerWidthPercentage { get; set; }

    [Range(0, 100)]
    public decimal? ShortRegimeMinPriceBelowMiddleBandPercentage { get; set; }

    [Range(0.01, 100)]
    public decimal MaxDailyLossPercentage { get; set; } = 1m;

    [Range(0, 3600)]
    public int PerBotCooldownSeconds { get; set; } = 300;

    [Range(0, 3600)]
    public int PerSymbolCooldownSeconds { get; set; } = 300;

    [Range(0, 100)]
    public int MaxOpenPositionsPerUser { get; set; } = 1;

    [Range(1, 3600)]
    public int PrivatePlaneFreshnessThresholdSeconds { get; set; } = 120;

    [Range(50, 1000)]
    public int PrimeHistoricalCandleCount { get; set; } = 200;

    public void NormalizeScopeCollections()
    {
        AllowedSymbols = ResolveNormalizedAllowedSymbols();
        if (AllowedExecutionSymbols is not null)
        {
            AllowedExecutionSymbols = ResolveNormalizedAllowedExecutionSymbols();
        }

        AllowedUserIds = ResolveNormalizedAllowedUserIds();
        AllowedBotIds = ResolveNormalizedAllowedBotIds();
    }

    internal string[] ResolveNormalizedAllowedSymbols()
    {
        return NormalizeSymbols(AllowedSymbols);
    }

    internal bool TryResolveNormalizedAllowedExecutionSymbols(out string[] allowedExecutionSymbols)
    {
        if (AllowedExecutionSymbols is null)
        {
            allowedExecutionSymbols = [];
            return false;
        }

        allowedExecutionSymbols = ResolveNormalizedAllowedExecutionSymbols();
        return true;
    }

    internal string[] ResolveNormalizedAllowedExecutionSymbols()
    {
        return NormalizeSymbols(AllowedExecutionSymbols);
    }

    private static string[] NormalizeSymbols(IEnumerable<string?>? values)
    {
        return (values ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal string[] ResolveNormalizedAllowedUserIds()
    {
        return AllowedUserIds
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal string[] ResolveNormalizedAllowedBotIds()
    {
        return AllowedBotIds
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(NormalizeAllowedBotId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    public int ResolveEntryHysteresisCooldownMinutes(StrategyTradeDirection direction)
    {
        return direction switch
        {
            StrategyTradeDirection.Long => LongEntryHysteresisCooldownMinutes ?? EntryHysteresisCooldownMinutes,
            StrategyTradeDirection.Short => ShortEntryHysteresisCooldownMinutes ?? EntryHysteresisCooldownMinutes,
            _ => EntryHysteresisCooldownMinutes
        };
    }

    public decimal ResolveEntryHysteresisReentryBufferPercentage(StrategyTradeDirection direction)
    {
        return direction switch
        {
            StrategyTradeDirection.Long => LongEntryHysteresisReentryBufferPercentage ?? EntryHysteresisReentryBufferPercentage,
            StrategyTradeDirection.Short => ShortEntryHysteresisReentryBufferPercentage ?? EntryHysteresisReentryBufferPercentage,
            _ => EntryHysteresisReentryBufferPercentage
        };
    }

    public decimal ResolveRegimeMaxEntryRsi(StrategyTradeDirection direction)
    {
        return direction switch
        {
            StrategyTradeDirection.Long => LongRegimeMaxEntryRsi ?? RegimeMaxEntryRsi,
            StrategyTradeDirection.Short => ShortRegimeMinEntryRsi ?? 0m,
            _ => 0m
        };
    }

    public decimal ResolveRegimeMacdThreshold(StrategyTradeDirection direction)
    {
        return direction switch
        {
            StrategyTradeDirection.Long => LongRegimeMinMacdHistogram ?? RegimeMinMacdHistogram,
            StrategyTradeDirection.Short => ShortRegimeMaxMacdHistogram ?? 0m,
            _ => 0m
        };
    }

    public decimal ResolveRegimeMinBollingerWidthPercentage(StrategyTradeDirection direction)
    {
        return direction switch
        {
            StrategyTradeDirection.Long => LongRegimeMinBollingerWidthPercentage ?? RegimeMinBollingerWidthPercentage,
            StrategyTradeDirection.Short => ShortRegimeMinBollingerWidthPercentage ?? RegimeMinBollingerWidthPercentage,
            _ => 0m
        };
    }

    public decimal ResolveRegimeMinMiddleBandDislocationPercentage(StrategyTradeDirection direction)
    {
        return direction switch
        {
            StrategyTradeDirection.Long => LongRegimeMinPriceAboveMiddleBandPercentage ?? RegimeMinPriceAboveMiddleBandPercentage,
            StrategyTradeDirection.Short => ShortRegimeMinPriceBelowMiddleBandPercentage ?? 0m,
            _ => 0m
        };
    }


    public bool IsRegimeAwareEntryDisciplineEnabled(StrategyTradeDirection direction)
    {
        return direction switch
        {
            StrategyTradeDirection.Long => LongRegimeFilterEnabled ?? EnableRegimeAwareEntryDiscipline,
            StrategyTradeDirection.Short => ShortRegimeFilterEnabled ?? EnableRegimeAwareEntryDiscipline,
            _ => EnableRegimeAwareEntryDiscipline
        };
    }

    public string BuildRegimeThresholdSummary(StrategyTradeDirection direction)
    {
        if (!IsRegimeAwareEntryDisciplineEnabled(direction))
        {
            return $"{direction} regime filter disabled.";
        }

        var parts = new List<string>();
        var rsiThreshold = ResolveRegimeMaxEntryRsi(direction);
        var macdThreshold = ResolveRegimeMacdThreshold(direction);
        var bollingerWidthThreshold = ResolveRegimeMinBollingerWidthPercentage(direction);
        var middleBandThreshold = ResolveRegimeMinMiddleBandDislocationPercentage(direction);

        if (direction == StrategyTradeDirection.Long)
        {
            if (rsiThreshold > 0m)
            {
                parts.Add($"RSI < {rsiThreshold.ToString("0.##", CultureInfo.InvariantCulture)}");
            }

            parts.Add($"MACD hist >= {macdThreshold.ToString("0.####", CultureInfo.InvariantCulture)}");

            if (bollingerWidthThreshold > 0m)
            {
                parts.Add($"Bollinger width >= {bollingerWidthThreshold.ToString("0.####", CultureInfo.InvariantCulture)}%");
            }

            if (middleBandThreshold > 0m)
            {
                parts.Add($"Price vs middle band >= {middleBandThreshold.ToString("0.####", CultureInfo.InvariantCulture)}%");
            }
        }
        else if (direction == StrategyTradeDirection.Short)
        {
            if (rsiThreshold > 0m)
            {
                parts.Add($"RSI > {rsiThreshold.ToString("0.##", CultureInfo.InvariantCulture)}");
            }

            parts.Add($"MACD hist <= {macdThreshold.ToString("0.####", CultureInfo.InvariantCulture)}");

            if (bollingerWidthThreshold > 0m)
            {
                parts.Add($"Bollinger width >= {bollingerWidthThreshold.ToString("0.####", CultureInfo.InvariantCulture)}%");
            }

            if (middleBandThreshold > 0m)
            {
                parts.Add($"Price vs middle band >= {middleBandThreshold.ToString("0.####", CultureInfo.InvariantCulture)}%");
            }
        }

        return parts.Count == 0
            ? $"{direction} regime filter enabled with default thresholds."
            : string.Join(" · ", parts);
    }


    public bool HasConfiguredMaxPilotOrderNotional()
    {
        return !string.IsNullOrWhiteSpace(MaxPilotOrderNotional);
    }

    public bool TryResolveMaxPilotOrderNotional(out decimal value)
    {
        value = 0m;
        var normalizedValue = MaxPilotOrderNotional?.Trim();

        return !string.IsNullOrWhiteSpace(normalizedValue) &&
               decimal.TryParse(
                   normalizedValue,
                   NumberStyles.Number,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static string NormalizeAllowedBotId(string value)
    {
        var normalized = value.Trim();
        return Guid.TryParse(normalized, out var botId)
            ? botId.ToString("N")
            : normalized;
    }
}
