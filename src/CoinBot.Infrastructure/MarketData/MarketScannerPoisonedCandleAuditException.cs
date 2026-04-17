namespace CoinBot.Infrastructure.MarketData;

public sealed class MarketScannerPoisonedCandleAuditException : InvalidOperationException
{
    public MarketScannerPoisonedCandleAuditException(
        int purgedCount,
        DateTime auditWindowStartUtc,
        DateTime auditWindowEndUtc,
        string? detail = null)
        : base(BuildMessage(purgedCount, auditWindowStartUtc, auditWindowEndUtc))
    {
        PurgedCount = Math.Max(0, purgedCount);
        AuditWindowStartUtc = DateTime.SpecifyKind(auditWindowStartUtc, DateTimeKind.Utc);
        AuditWindowEndUtc = DateTime.SpecifyKind(auditWindowEndUtc, DateTimeKind.Utc);
        ErrorCode = "ScannerPoisonedCandleAudit";
        Detail = string.IsNullOrWhiteSpace(detail)
            ? $"ErrorCode={ErrorCode}; PurgedCount={PurgedCount}; AuditWindowStartUtc={AuditWindowStartUtc:O}; AuditWindowEndUtc={AuditWindowEndUtc:O}"
            : detail.Trim();
    }

    public int PurgedCount { get; }

    public DateTime AuditWindowStartUtc { get; }

    public DateTime AuditWindowEndUtc { get; }

    public string ErrorCode { get; }

    public string Detail { get; }

    private static string BuildMessage(int purgedCount, DateTime auditWindowStartUtc, DateTime auditWindowEndUtc)
    {
        return $"Market scanner poisoned candle audit removed {Math.Max(0, purgedCount)} historical candles. AuditWindowStartUtc={DateTime.SpecifyKind(auditWindowStartUtc, DateTimeKind.Utc):O}; AuditWindowEndUtc={DateTime.SpecifyKind(auditWindowEndUtc, DateTimeKind.Utc):O}";
    }
}
