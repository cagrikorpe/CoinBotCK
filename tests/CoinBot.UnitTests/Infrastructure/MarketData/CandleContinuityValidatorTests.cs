using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.MarketData;

public sealed class CandleContinuityValidatorTests
{
    private readonly CandleContinuityValidator validator = new(NullLogger<CandleContinuityValidator>.Instance);

    [Fact]
    public void Validate_RejectsDuplicateClosedCandle()
    {
        var first = CreateClosedCandleSnapshot("BTCUSDT", new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc));
        var duplicate = CreateClosedCandleSnapshot("BTCUSDT", new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc));

        var firstResult = validator.Validate(first);
        var duplicateResult = validator.Validate(duplicate);

        Assert.True(firstResult.IsAccepted);
        Assert.False(duplicateResult.IsAccepted);
        Assert.Equal(DegradedModeReasonCode.CandleDataDuplicateDetected, duplicateResult.GuardReasonCode);
        Assert.Equal(first.CloseTimeUtc, duplicateResult.EffectiveDataTimestampUtc);
    }

    [Fact]
    public void Validate_RejectsOutOfOrderClosedCandle()
    {
        var first = CreateClosedCandleSnapshot("BTCUSDT", new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc));
        var outOfOrder = CreateClosedCandleSnapshot("BTCUSDT", new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc));

        validator.Validate(first);
        var result = validator.Validate(outOfOrder);

        Assert.False(result.IsAccepted);
        Assert.Equal(DegradedModeReasonCode.CandleDataOutOfOrderDetected, result.GuardReasonCode);
        Assert.Equal(first.CloseTimeUtc, result.EffectiveDataTimestampUtc);
    }

    [Fact]
    public void Validate_RejectsGapClosedCandle_AndKeepsExpectedOpenTime()
    {
        var first = CreateClosedCandleSnapshot("BTCUSDT", new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc));
        var gap = CreateClosedCandleSnapshot("BTCUSDT", new DateTime(2026, 3, 22, 12, 2, 0, DateTimeKind.Utc));

        validator.Validate(first);
        var result = validator.Validate(gap);

        Assert.False(result.IsAccepted);
        Assert.Equal(DegradedModeReasonCode.CandleDataGapDetected, result.GuardReasonCode);
        Assert.Equal(new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc), result.ExpectedOpenTimeUtc);
        Assert.Equal(first.CloseTimeUtc, result.EffectiveDataTimestampUtc);
    }

    private static MarketCandleSnapshot CreateClosedCandleSnapshot(string symbol, DateTime openTimeUtc)
    {
        return new MarketCandleSnapshot(
            symbol,
            "1m",
            openTimeUtc,
            openTimeUtc.AddMinutes(1).AddMilliseconds(-1),
            OpenPrice: 1m,
            HighPrice: 1m,
            LowPrice: 1m,
            ClosePrice: 1m,
            Volume: 1m,
            IsClosed: true,
            ReceivedAtUtc: openTimeUtc.AddMinutes(1),
            Source: "Binance.WebSocket.Kline");
    }
}
