using System.ComponentModel.DataAnnotations;
using System.Globalization;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BotExecutionPilotOptions
{
    public bool Enabled { get; set; }

    public bool PilotActivationEnabled { get; set; } = false;

    public ExecutionEnvironment SignalEvaluationMode { get; set; } = ExecutionEnvironment.Live;

    [Required]
    public string DefaultSymbol { get; set; } = "BTCUSDT";

    [Required]
    public string Timeframe { get; set; } = "1m";

    [Range(1, 125)]
    public decimal DefaultLeverage { get; set; } = 1m;

    [Required]
    public string DefaultMarginType { get; set; } = "ISOLATED";

    public string[] AllowedSymbols { get; set; } = ["BTCUSDT"];

    public string[] AllowedUserIds { get; set; } = [];

    public string[] AllowedBotIds { get; set; } = [];

    public string? MaxPilotOrderNotional { get; set; }

    public decimal? MaxOrderNotional
    {
        get => TryResolveMaxPilotOrderNotional(out var value) ? value : null;
        set => MaxPilotOrderNotional = value?.ToString("0.############################", CultureInfo.InvariantCulture);
    }

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
}
