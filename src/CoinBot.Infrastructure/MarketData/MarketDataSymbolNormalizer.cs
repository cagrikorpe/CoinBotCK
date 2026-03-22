namespace CoinBot.Infrastructure.MarketData;

internal static class MarketDataSymbolNormalizer
{
    public static string Normalize(string? symbol)
    {
        var normalizedSymbol = symbol?.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(normalizedSymbol))
        {
            throw new ArgumentException("The symbol is required.", nameof(symbol));
        }

        return normalizedSymbol;
    }

    public static IReadOnlyCollection<string> NormalizeMany(IEnumerable<string> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);

        var normalizedSymbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            normalizedSymbols.Add(Normalize(symbol));
        }

        return normalizedSymbols
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();
    }
}
