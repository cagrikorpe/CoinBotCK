using System.Globalization;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Ai;

public sealed partial class AiShadowDecisionService
{
    private const decimal DirectionalScoreSaturationReturn = 0.005m;
    private const decimal NeutralBandReturn = 0.001m;
    private const decimal NeutralFailureSaturationReturn = 0.005m;

    public async Task<AiShadowDecisionOutcomeSnapshot> ScoreOutcomeAsync(
        string userId,
        Guid decisionId,
        AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind,
        int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = dbContext.EnsureCurrentUserScope(userId);
        var normalizedHorizonValue = NormalizeHorizonValue(horizonValue);
        ValidateHorizonKind(horizonKind);

        var decision = await dbContext.AiShadowDecisions
            .SingleOrDefaultAsync(entity =>
                entity.OwnerUserId == normalizedUserId &&
                entity.Id == decisionId &&
                !entity.IsDeleted,
                cancellationToken)
            ?? throw new InvalidOperationException($"AI shadow decision '{decisionId}' was not found.");

        var outcome = await UpsertOutcomeAsync(decision, horizonKind, normalizedHorizonValue, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapOutcome(outcome);
    }

    public async Task<int> EnsureOutcomeCoverageAsync(
        string userId,
        AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind,
        int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue,
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = dbContext.EnsureCurrentUserScope(userId);
        var normalizedHorizonValue = NormalizeHorizonValue(horizonValue);
        ValidateHorizonKind(horizonKind);
        var normalizedTake = take <= 0 ? 200 : Math.Min(take, 1000);

        var decisions = await dbContext.AiShadowDecisions
            .Where(entity => entity.OwnerUserId == normalizedUserId && !entity.IsDeleted)
            .OrderByDescending(entity => entity.EvaluatedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .Take(normalizedTake)
            .ToListAsync(cancellationToken);

        var changedCount = 0;
        foreach (var decision in decisions)
        {
            var existingOutcome = await dbContext.AiShadowDecisionOutcomes
                .SingleOrDefaultAsync(entity =>
                    entity.OwnerUserId == normalizedUserId &&
                    entity.AiShadowDecisionId == decision.Id &&
                    entity.HorizonKind == horizonKind &&
                    entity.HorizonValue == normalizedHorizonValue &&
                    !entity.IsDeleted,
                    cancellationToken);
            var beforeSignature = existingOutcome is null ? null : CreateOutcomeSignature(existingOutcome);
            var updatedOutcome = await UpsertOutcomeAsync(decision, horizonKind, normalizedHorizonValue, cancellationToken, existingOutcome);
            var afterSignature = CreateOutcomeSignature(updatedOutcome);

            if (!string.Equals(beforeSignature, afterSignature, StringComparison.Ordinal))
            {
                changedCount++;
            }
        }

        if (changedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return changedCount;
    }
    public async Task<AiShadowDecisionOutcomeSummarySnapshot> GetOutcomeSummaryAsync(
        string userId,
        AiShadowOutcomeHorizonKind horizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind,
        int horizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue,
        int take = 200,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserId = dbContext.EnsureCurrentUserScope(userId);
        var normalizedHorizonValue = NormalizeHorizonValue(horizonValue);
        ValidateHorizonKind(horizonKind);
        var normalizedTake = take <= 0 ? 200 : Math.Min(take, 1000);

        await EnsureOutcomeCoverageAsync(normalizedUserId, horizonKind, normalizedHorizonValue, normalizedTake, cancellationToken);

        var decisionIds = await dbContext.AiShadowDecisions
            .AsNoTracking()
            .Where(entity => entity.OwnerUserId == normalizedUserId && !entity.IsDeleted)
            .OrderByDescending(entity => entity.EvaluatedAtUtc)
            .ThenByDescending(entity => entity.CreatedDate)
            .Take(normalizedTake)
            .Select(entity => entity.Id)
            .ToArrayAsync(cancellationToken);

        var rows = decisionIds.Length == 0
            ? []
            : await dbContext.AiShadowDecisionOutcomes
                .AsNoTracking()
                .Where(entity =>
                    entity.OwnerUserId == normalizedUserId &&
                    decisionIds.Contains(entity.AiShadowDecisionId) &&
                    entity.HorizonKind == horizonKind &&
                    entity.HorizonValue == normalizedHorizonValue &&
                    !entity.IsDeleted)
                .ToListAsync(cancellationToken);

        var scoredRows = rows.Where(IsScoredOutcome).ToArray();
        var averageOutcomeScore = scoredRows.Length == 0
            ? 0m
            : decimal.Round(scoredRows.Average(entity => entity.OutcomeScore ?? 0m), 4, MidpointRounding.AwayFromZero);

        return new AiShadowDecisionOutcomeSummarySnapshot(
            normalizedUserId,
            horizonKind,
            normalizedHorizonValue,
            decisionIds.Length,
            scoredRows.Length,
            rows.Count(entity => entity.OutcomeState == AiShadowOutcomeState.FutureDataUnavailable),
            rows.Count(entity => entity.OutcomeState == AiShadowOutcomeState.ReferenceDataUnavailable),
            averageOutcomeScore,
            scoredRows.Count(entity => (entity.OutcomeScore ?? 0m) > 0m),
            scoredRows.Count(entity => (entity.OutcomeScore ?? 0m) < 0m),
            scoredRows.Count(entity => string.Equals(entity.RealizedDirectionality, "Neutral", StringComparison.Ordinal)),
            scoredRows.Count(entity => entity.FalsePositive),
            scoredRows.Count(entity => entity.FalseNeutral),
            scoredRows.Count(entity => entity.Overtrading),
            scoredRows.Count(entity => entity.SuppressionCandidate && entity.SuppressionAligned),
            scoredRows.Count(entity => entity.SuppressionCandidate && !entity.SuppressionAligned),
            BuildConfidenceBuckets(rows),
            BuildBuckets(rows.Select(entity => entity.OutcomeState.ToString())),
            BuildBuckets(rows.Select(entity => entity.FutureDataAvailability.ToString())));
    }

    private async Task<AiShadowDecisionOutcome> UpsertOutcomeAsync(
        AiShadowDecision decision,
        AiShadowOutcomeHorizonKind horizonKind,
        int horizonValue,
        CancellationToken cancellationToken,
        AiShadowDecisionOutcome? existingOutcome = null)
    {
        var outcome = existingOutcome;
        if (outcome is null)
        {
            outcome = await dbContext.AiShadowDecisionOutcomes
                .SingleOrDefaultAsync(entity =>
                    entity.OwnerUserId == decision.OwnerUserId &&
                    entity.AiShadowDecisionId == decision.Id &&
                    entity.HorizonKind == horizonKind &&
                    entity.HorizonValue == horizonValue &&
                    !entity.IsDeleted,
                    cancellationToken);
        }

        if (outcome is null)
        {
            outcome = new AiShadowDecisionOutcome
            {
                Id = Guid.NewGuid(),
                OwnerUserId = decision.OwnerUserId,
                AiShadowDecisionId = decision.Id,
                BotId = decision.BotId,
                Symbol = decision.Symbol,
                Timeframe = decision.Timeframe,
                DecisionEvaluatedAtUtc = decision.EvaluatedAtUtc,
                HorizonKind = horizonKind,
                HorizonValue = horizonValue
            };
            dbContext.AiShadowDecisionOutcomes.Add(outcome);
        }

        var scoringSnapshot = await ComputeOutcomeAsync(decision, horizonKind, horizonValue, cancellationToken);
        outcome.BotId = decision.BotId;
        outcome.Symbol = decision.Symbol;
        outcome.Timeframe = decision.Timeframe;
        outcome.DecisionEvaluatedAtUtc = decision.EvaluatedAtUtc;
        outcome.HorizonKind = horizonKind;
        outcome.HorizonValue = horizonValue;
        outcome.OutcomeState = scoringSnapshot.OutcomeState;
        outcome.OutcomeScore = scoringSnapshot.OutcomeScore;
        outcome.RealizedDirectionality = scoringSnapshot.RealizedDirectionality;
        outcome.ConfidenceBucket = scoringSnapshot.ConfidenceBucket;
        outcome.FutureDataAvailability = scoringSnapshot.FutureDataAvailability;
        outcome.ReferenceCandleCloseTimeUtc = scoringSnapshot.ReferenceCandleCloseTimeUtc;
        outcome.FutureCandleCloseTimeUtc = scoringSnapshot.FutureCandleCloseTimeUtc;
        outcome.ReferenceClosePrice = scoringSnapshot.ReferenceClosePrice;
        outcome.FutureClosePrice = scoringSnapshot.FutureClosePrice;
        outcome.RealizedReturn = scoringSnapshot.RealizedReturn;
        outcome.FalsePositive = scoringSnapshot.FalsePositive;
        outcome.FalseNeutral = scoringSnapshot.FalseNeutral;
        outcome.Overtrading = scoringSnapshot.Overtrading;
        outcome.SuppressionCandidate = scoringSnapshot.SuppressionCandidate;
        outcome.SuppressionAligned = scoringSnapshot.SuppressionAligned;
        outcome.ScoredAtUtc = scoringSnapshot.ScoredAtUtc;

        return outcome;
    }
    private async Task<ComputedOutcomeSnapshot> ComputeOutcomeAsync(
        AiShadowDecision decision,
        AiShadowOutcomeHorizonKind horizonKind,
        int horizonValue,
        CancellationToken cancellationToken)
    {
        ValidateHorizonKind(horizonKind);
        var anchorTimestampUtc = NormalizeTimestamp(decision.MarketDataTimestampUtc ?? decision.EvaluatedAtUtc);
        var referenceCandle = await ResolveReferenceCandleAsync(decision.Symbol, decision.Timeframe, anchorTimestampUtc, cancellationToken);
        var confidenceBucket = ResolveConfidenceBucket(decision.AiConfidence);
        var suppressionCandidate = ResolveSuppressionCandidate(decision);
        var scoredAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        if (referenceCandle is null)
        {
            return new ComputedOutcomeSnapshot(
                AiShadowOutcomeState.ReferenceDataUnavailable,
                null,
                "Unknown",
                confidenceBucket,
                AiShadowFutureDataAvailability.MissingReferenceCandle,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                false,
                suppressionCandidate,
                false,
                scoredAtUtc);
        }

        if (referenceCandle.ClosePrice <= 0m)
        {
            return new ComputedOutcomeSnapshot(
                AiShadowOutcomeState.ReferenceDataUnavailable,
                null,
                "Unknown",
                confidenceBucket,
                AiShadowFutureDataAvailability.InvalidReferencePrice,
                referenceCandle.CloseTimeUtc,
                null,
                referenceCandle.ClosePrice,
                null,
                null,
                false,
                false,
                false,
                suppressionCandidate,
                false,
                scoredAtUtc);
        }

        var futureCandle = await ResolveFutureCandleAsync(decision.Symbol, decision.Timeframe, referenceCandle.CloseTimeUtc, horizonValue, cancellationToken);
        if (futureCandle is null)
        {
            return new ComputedOutcomeSnapshot(
                AiShadowOutcomeState.FutureDataUnavailable,
                null,
                "Unknown",
                confidenceBucket,
                AiShadowFutureDataAvailability.MissingFutureCandle,
                referenceCandle.CloseTimeUtc,
                null,
                referenceCandle.ClosePrice,
                null,
                null,
                false,
                false,
                false,
                suppressionCandidate,
                false,
                scoredAtUtc);
        }

        var realizedReturn = decimal.Round(
            (futureCandle.ClosePrice - referenceCandle.ClosePrice) / referenceCandle.ClosePrice,
            8,
            MidpointRounding.AwayFromZero);
        var realizedDirectionality = ResolveRealizedDirectionality(realizedReturn);
        var outcomeScore = ResolveOutcomeScore(decision.AiDirection, realizedReturn);
        var falsePositive = ResolveFalsePositive(decision.AiDirection, outcomeScore);
        var falseNeutral = ResolveFalseNeutral(decision.AiDirection, realizedReturn);
        var overtrading = ResolveOvertrading(decision.AiDirection, realizedReturn);
        var suppressionAligned = ResolveSuppressionAligned(decision.AiDirection, suppressionCandidate, outcomeScore, overtrading);

        return new ComputedOutcomeSnapshot(
            AiShadowOutcomeState.Scored,
            outcomeScore,
            realizedDirectionality,
            confidenceBucket,
            AiShadowFutureDataAvailability.Available,
            referenceCandle.CloseTimeUtc,
            futureCandle.CloseTimeUtc,
            referenceCandle.ClosePrice,
            futureCandle.ClosePrice,
            realizedReturn,
            falsePositive,
            falseNeutral,
            overtrading,
            suppressionCandidate,
            suppressionAligned,
            scoredAtUtc);
    }

    private async Task<HistoricalMarketCandle?> ResolveReferenceCandleAsync(
        string symbol,
        string timeframe,
        DateTime anchorTimestampUtc,
        CancellationToken cancellationToken)
    {
        return await dbContext.HistoricalMarketCandles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Symbol == symbol &&
                entity.Interval == timeframe &&
                entity.CloseTimeUtc <= anchorTimestampUtc)
            .OrderByDescending(entity => entity.CloseTimeUtc)
            .ThenByDescending(entity => entity.ReceivedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<HistoricalMarketCandle?> ResolveFutureCandleAsync(
        string symbol,
        string timeframe,
        DateTime referenceCloseTimeUtc,
        int horizonValue,
        CancellationToken cancellationToken)
    {
        var candidates = await dbContext.HistoricalMarketCandles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.Symbol == symbol &&
                entity.Interval == timeframe &&
                entity.CloseTimeUtc > referenceCloseTimeUtc)
            .OrderBy(entity => entity.CloseTimeUtc)
            .ThenByDescending(entity => entity.ReceivedAtUtc)
            .Take(Math.Max(horizonValue * 8, 32))
            .ToListAsync(cancellationToken);

        return candidates
            .GroupBy(entity => NormalizeTimestamp(entity.CloseTimeUtc))
            .OrderBy(group => group.Key)
            .Select(group => group.OrderByDescending(entity => entity.ReceivedAtUtc).First())
            .Skip(horizonValue - 1)
            .FirstOrDefault();
    }
    private static IReadOnlyCollection<AiShadowOutcomeConfidenceBucketSnapshot> BuildConfidenceBuckets(IEnumerable<AiShadowDecisionOutcome> rows)
    {
        return rows
            .GroupBy(entity => entity.ConfidenceBucket, StringComparer.Ordinal)
            .OrderBy(group => ResolveConfidenceBucketSortOrder(group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var scoredRows = group.Where(IsScoredOutcome).ToArray();
                var averageOutcomeScore = scoredRows.Length == 0
                    ? 0m
                    : decimal.Round(scoredRows.Average(entity => entity.OutcomeScore ?? 0m), 4, MidpointRounding.AwayFromZero);

                return new AiShadowOutcomeConfidenceBucketSnapshot(
                    group.Key,
                    group.Count(),
                    scoredRows.Length,
                    scoredRows.Count(IsSuccessfulOutcome),
                    scoredRows.Count(entity => entity.FalsePositive),
                    scoredRows.Count(entity => entity.FalseNeutral),
                    scoredRows.Count(entity => entity.Overtrading),
                    averageOutcomeScore);
            })
            .ToArray();
    }

    private static int ResolveConfidenceBucketSortOrder(string bucket)
    {
        return bucket switch
        {
            "High" => 0,
            "Medium" => 1,
            _ => 2
        };
    }

    private static bool IsScoredOutcome(AiShadowDecisionOutcome entity)
    {
        return entity.OutcomeState == AiShadowOutcomeState.Scored && entity.OutcomeScore.HasValue;
    }

    private static bool IsSuccessfulOutcome(AiShadowDecisionOutcome entity)
    {
        if (!IsScoredOutcome(entity))
        {
            return false;
        }

        if (string.Equals(entity.RealizedDirectionality, "Neutral", StringComparison.Ordinal))
        {
            return (entity.OutcomeScore ?? 0m) >= 0m;
        }

        return (entity.OutcomeScore ?? 0m) > 0m;
    }

    private static AiShadowDecisionOutcomeSnapshot MapOutcome(AiShadowDecisionOutcome entity)
    {
        return new AiShadowDecisionOutcomeSnapshot(
            entity.Id,
            entity.AiShadowDecisionId,
            entity.OwnerUserId,
            entity.BotId,
            entity.Symbol,
            entity.Timeframe,
            entity.DecisionEvaluatedAtUtc,
            entity.HorizonKind,
            entity.HorizonValue,
            entity.OutcomeState,
            entity.OutcomeScore,
            entity.RealizedDirectionality,
            entity.ConfidenceBucket,
            entity.FutureDataAvailability,
            entity.ReferenceCandleCloseTimeUtc,
            entity.FutureCandleCloseTimeUtc,
            entity.ReferenceClosePrice,
            entity.FutureClosePrice,
            entity.RealizedReturn,
            entity.FalsePositive,
            entity.FalseNeutral,
            entity.Overtrading,
            entity.SuppressionCandidate,
            entity.SuppressionAligned,
            entity.ScoredAtUtc,
            entity.CreatedDate);
    }

    private static string CreateOutcomeSignature(AiShadowDecisionOutcome entity)
    {
        return string.Join("|",
            entity.HorizonKind,
            entity.HorizonValue,
            entity.OutcomeState,
            entity.OutcomeScore?.ToString("0.########", CultureInfo.InvariantCulture) ?? "n/a",
            entity.RealizedDirectionality,
            entity.ConfidenceBucket,
            entity.FutureDataAvailability,
            entity.ReferenceCandleCloseTimeUtc?.ToString("O") ?? "n/a",
            entity.FutureCandleCloseTimeUtc?.ToString("O") ?? "n/a",
            entity.ReferenceClosePrice?.ToString("0.########", CultureInfo.InvariantCulture) ?? "n/a",
            entity.FutureClosePrice?.ToString("0.########", CultureInfo.InvariantCulture) ?? "n/a",
            entity.RealizedReturn?.ToString("0.########", CultureInfo.InvariantCulture) ?? "n/a",
            entity.FalsePositive,
            entity.FalseNeutral,
            entity.Overtrading,
            entity.SuppressionCandidate,
            entity.SuppressionAligned);
    }

    private static void ValidateHorizonKind(AiShadowOutcomeHorizonKind horizonKind)
    {
        if (horizonKind != AiShadowOutcomeHorizonKind.BarsForward)
        {
            throw new ArgumentOutOfRangeException(nameof(horizonKind), horizonKind, "Only BarsForward scoring is supported.");
        }
    }

    private static int NormalizeHorizonValue(int horizonValue)
    {
        if (horizonValue <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(horizonValue), horizonValue, "The horizon value must be greater than zero.");
        }

        return Math.Min(horizonValue, 32);
    }

    private static string ResolveConfidenceBucket(decimal aiConfidence) =>
        aiConfidence >= 0.70m ? "High" : aiConfidence >= 0.40m ? "Medium" : "Low";

    private static string ResolveRealizedDirectionality(decimal realizedReturn)
    {
        var absoluteReturn = Math.Abs(realizedReturn);
        if (absoluteReturn <= NeutralBandReturn)
        {
            return "Neutral";
        }

        return realizedReturn > 0m ? "Long" : "Short";
    }

    private static decimal ResolveOutcomeScore(string aiDirection, decimal realizedReturn)
    {
        var normalizedDirection = NormalizeDirection(aiDirection);
        return normalizedDirection switch
        {
            "Long" => ClampScore(realizedReturn / DirectionalScoreSaturationReturn),
            "Short" => ClampScore((-realizedReturn) / DirectionalScoreSaturationReturn),
            _ => ResolveNeutralOutcomeScore(realizedReturn)
        };
    }

    private static decimal ResolveNeutralOutcomeScore(decimal realizedReturn)
    {
        var absoluteReturn = Math.Abs(realizedReturn);
        if (absoluteReturn <= NeutralBandReturn)
        {
            return decimal.Round(1m - (absoluteReturn / NeutralBandReturn), 6, MidpointRounding.AwayFromZero);
        }

        var denominator = NeutralFailureSaturationReturn - NeutralBandReturn;
        var overflow = absoluteReturn - NeutralBandReturn;
        return decimal.Round(-ClampScore(denominator <= 0m ? 1m : overflow / denominator), 6, MidpointRounding.AwayFromZero);
    }

    private static decimal ClampScore(decimal rawValue)
    {
        if (rawValue < -1m)
        {
            return -1m;
        }

        if (rawValue > 1m)
        {
            return 1m;
        }

        return decimal.Round(rawValue, 6, MidpointRounding.AwayFromZero);
    }

    private static bool ResolveFalsePositive(string aiDirection, decimal outcomeScore)
    {
        var normalizedDirection = NormalizeDirection(aiDirection);
        return normalizedDirection is "Long" or "Short" && outcomeScore < 0m;
    }

    private static bool ResolveFalseNeutral(string aiDirection, decimal realizedReturn)
    {
        return NormalizeDirection(aiDirection) == "Neutral" && Math.Abs(realizedReturn) > NeutralBandReturn;
    }

    private static bool ResolveOvertrading(string aiDirection, decimal realizedReturn)
    {
        var normalizedDirection = NormalizeDirection(aiDirection);
        return normalizedDirection is "Long" or "Short" && Math.Abs(realizedReturn) <= NeutralBandReturn;
    }

    private static bool ResolveSuppressionCandidate(AiShadowDecision decision)
    {
        return string.Equals(decision.FinalAction, "NoSubmit", StringComparison.Ordinal) ||
               decision.RiskVetoPresent ||
               decision.PilotSafetyBlocked ||
               !decision.HypotheticalSubmitAllowed;
    }

    private static bool ResolveSuppressionAligned(string aiDirection, bool suppressionCandidate, decimal outcomeScore, bool overtrading)
    {
        if (!suppressionCandidate)
        {
            return false;
        }

        return NormalizeDirection(aiDirection) == "Neutral"
            ? outcomeScore >= 0m
            : outcomeScore <= 0m || overtrading;
    }

    private sealed record ComputedOutcomeSnapshot(
        AiShadowOutcomeState OutcomeState,
        decimal? OutcomeScore,
        string RealizedDirectionality,
        string ConfidenceBucket,
        AiShadowFutureDataAvailability FutureDataAvailability,
        DateTime? ReferenceCandleCloseTimeUtc,
        DateTime? FutureCandleCloseTimeUtc,
        decimal? ReferenceClosePrice,
        decimal? FutureClosePrice,
        decimal? RealizedReturn,
        bool FalsePositive,
        bool FalseNeutral,
        bool Overtrading,
        bool SuppressionCandidate,
        bool SuppressionAligned,
        DateTime ScoredAtUtc);
}
