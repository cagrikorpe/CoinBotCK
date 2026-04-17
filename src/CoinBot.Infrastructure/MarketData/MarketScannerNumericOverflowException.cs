using System.Globalization;

namespace CoinBot.Infrastructure.MarketData;

public sealed class MarketScannerNumericOverflowException : InvalidOperationException
{
    public MarketScannerNumericOverflowException(
        Guid scanCycleId,
        string symbol,
        string fieldName,
        decimal value,
        string? detail = null)
        : base(BuildMessage(scanCycleId, symbol, fieldName, value))
    {
        ScanCycleId = scanCycleId;
        Symbol = string.IsNullOrWhiteSpace(symbol)
            ? "n/a"
            : symbol.Trim().ToUpperInvariant();
        FieldName = string.IsNullOrWhiteSpace(fieldName)
            ? "Unknown"
            : fieldName.Trim();
        Value = value;
        ErrorCode = "ScannerNumericOverflow";
        Detail = string.IsNullOrWhiteSpace(detail)
            ? $"ErrorCode={ErrorCode}; ScanCycleId={ScanCycleId}; Symbol={Symbol}; Field={FieldName}; Value={Value.ToString(CultureInfo.InvariantCulture)}"
            : detail.Trim();
    }

    public Guid ScanCycleId { get; }

    public string Symbol { get; }

    public string FieldName { get; }

    public decimal Value { get; }

    public string ErrorCode { get; }

    public string Detail { get; }

    private static string BuildMessage(Guid scanCycleId, string symbol, string fieldName, decimal value)
    {
        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol)
            ? "n/a"
            : symbol.Trim().ToUpperInvariant();
        var normalizedField = string.IsNullOrWhiteSpace(fieldName)
            ? "Unknown"
            : fieldName.Trim();

        return $"Market scanner numeric envelope rejected persistence. ScanCycleId={scanCycleId}; Symbol={normalizedSymbol}; Field={normalizedField}; Value={value.ToString(CultureInfo.InvariantCulture)}";
    }
}
