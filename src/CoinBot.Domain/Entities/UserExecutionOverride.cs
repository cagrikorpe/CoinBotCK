namespace CoinBot.Domain.Entities;

public sealed class UserExecutionOverride : BaseEntity
{
    public string UserId { get; set; } = string.Empty;

    public string? AllowedSymbolsCsv { get; set; }

    public string? DeniedSymbolsCsv { get; set; }

    public decimal? LeverageCap { get; set; }

    public decimal? MaxOrderSize { get; set; }

    public int? MaxDailyTrades { get; set; }

    public bool ReduceOnly { get; set; }

    public bool SessionDisabled { get; set; }
}
