using System.ComponentModel.DataAnnotations;
using CoinBot.Domain.Enums;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BotExecutionPilotOptions
{
    public bool Enabled { get; set; }

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

    [Range(0.00000001, 1000000)]
    public decimal MaxOrderNotional { get; set; } = 25m;

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
}
