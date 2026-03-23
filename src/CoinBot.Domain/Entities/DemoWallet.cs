namespace CoinBot.Domain.Entities;

public sealed class DemoWallet : UserOwnedEntity
{
    public string Asset { get; set; } = string.Empty;

    public decimal AvailableBalance { get; set; }

    public decimal ReservedBalance { get; set; }

    public string? ReferenceSymbol { get; set; }

    public string? ReferenceQuoteAsset { get; set; }

    public decimal? LastReferencePrice { get; set; }

    public decimal? AvailableValueInReferenceQuote { get; set; }

    public decimal? ReservedValueInReferenceQuote { get; set; }

    public DateTime? LastValuationAtUtc { get; set; }

    public string? LastValuationSource { get; set; }

    public DateTime LastActivityAtUtc { get; set; }

    public decimal TotalBalance => AvailableBalance + ReservedBalance;

    public decimal TotalValueInReferenceQuote =>
        (AvailableValueInReferenceQuote ?? 0m) +
        (ReservedValueInReferenceQuote ?? 0m);
}
