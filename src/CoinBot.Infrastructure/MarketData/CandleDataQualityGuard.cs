using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.MarketData;

public sealed class CandleDataQualityGuard(
    CandleContinuityValidator continuityValidator,
    ILogger<CandleDataQualityGuard> logger)
{
    public CandleDataQualityGuardResult Evaluate(MarketCandleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var continuityResult = continuityValidator.Validate(snapshot);

        if (continuityResult.IsAccepted)
        {
            return new CandleDataQualityGuardResult(
                IsAccepted: true,
                GuardStateCode: DegradedModeStateCode.Normal,
                GuardReasonCode: DegradedModeReasonCode.None,
                EffectiveDataTimestampUtc: continuityResult.EffectiveDataTimestampUtc,
                ExpectedOpenTimeUtc: continuityResult.ExpectedOpenTimeUtc);
        }

        logger.LogWarning(
            "Candle data quality guard rejected {Symbol} {Interval} with reason {ReasonCode}.",
            snapshot.Symbol,
            snapshot.Interval,
            continuityResult.GuardReasonCode);

        return new CandleDataQualityGuardResult(
            IsAccepted: false,
            GuardStateCode: continuityResult.GuardStateCode,
            GuardReasonCode: continuityResult.GuardReasonCode,
            EffectiveDataTimestampUtc: continuityResult.EffectiveDataTimestampUtc,
            ExpectedOpenTimeUtc: continuityResult.ExpectedOpenTimeUtc);
    }
}
