using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Ai;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Ai;

public sealed class AiShadowDecisionServiceTests
{
    [Fact]
    public async Task CaptureAsync_PersistsStructuredShadowDecision_AndReadsBackLatest()
    {
        await using var dbContext = CreateDbContext();
        var service = new AiShadowDecisionService(dbContext, TimeProvider.System);
        var botId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var featureSnapshotId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var nowUtc = new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc);

        var persisted = await service.CaptureAsync(
            CreateRequest(
                id: Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                botId: botId,
                featureSnapshotId: featureSnapshotId,
                evaluatedAtUtc: nowUtc,
                finalAction: "ShadowOnly",
                hypotheticalSubmitAllowed: true,
                hypotheticalBlockReason: null,
                noSubmitReason: "ShadowModeActive",
                aiDirection: "Long",
                aiConfidence: 0.82m,
                aiIsFallback: false,
                aiFallbackReason: null,
                riskVetoPresent: false,
                pilotSafetyBlocked: false,
                agreementState: "Agreement"));

        var latest = await service.GetLatestAsync("shadow-user", botId, "BTCUSDT", "1m");
        var recent = await service.ListRecentAsync("shadow-user", botId, "BTCUSDT", "1m");

        Assert.NotNull(latest);
        Assert.Equal(persisted.Id, latest!.Id);
        Assert.Equal(featureSnapshotId, latest.FeatureSnapshotId);
        Assert.Equal("ShadowOnly", latest.FinalAction);
        Assert.True(latest.HypotheticalSubmitAllowed);
        Assert.Equal("ShadowModeActive", latest.NoSubmitReason);
        Assert.Single(recent);
    }

    [Fact]
    public async Task GetSummaryAsync_AggregatesDirectionFallbackRiskPilotAndNoSubmitReasons()
    {
        await using var dbContext = CreateDbContext();
        var service = new AiShadowDecisionService(dbContext, TimeProvider.System);
        var botId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var nowUtc = new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc);

        await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: botId,
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc,
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Long",
            aiConfidence: 0.80m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: botId,
            featureSnapshotId: null,
            evaluatedAtUtc: nowUtc.AddMinutes(1),
            finalAction: "NoSubmit",
            hypotheticalSubmitAllowed: false,
            hypotheticalBlockReason: "TradeMasterDisarmed",
            noSubmitReason: "TradeMasterDisarmed",
            aiDirection: "Neutral",
            aiConfidence: 0m,
            aiIsFallback: true,
            aiFallbackReason: nameof(AiSignalFallbackReason.FeatureSnapshotUnavailable),
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Disagreement"));
        await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: botId,
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc.AddMinutes(2),
            finalAction: "NoSubmit",
            hypotheticalSubmitAllowed: false,
            hypotheticalBlockReason: "UserExecutionPilotNotionalLimitExceeded",
            noSubmitReason: "UserExecutionPilotNotionalLimitExceeded",
            aiDirection: "Short",
            aiConfidence: 0.35m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: true,
            pilotSafetyBlocked: true,
            agreementState: "Disagreement"));

        var summary = await service.GetSummaryAsync("shadow-user", botId, "BTCUSDT", "1m");

        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(1, summary.ShadowOnlyCount);
        Assert.Equal(2, summary.NoSubmitCount);
        Assert.Equal(1, summary.LongCount);
        Assert.Equal(1, summary.ShortCount);
        Assert.Equal(1, summary.NeutralCount);
        Assert.Equal(1, summary.FallbackCount);
        Assert.Equal(1, summary.RiskVetoCount);
        Assert.Equal(1, summary.PilotSafetyBlockCount);
        Assert.Equal(1, summary.AgreementCount);
        Assert.Equal(2, summary.DisagreementCount);
        Assert.Contains(summary.NoSubmitReasons, item => item.Key == "TradeMasterDisarmed" && item.Count == 1);
        Assert.Contains(summary.HypotheticalBlockReasons, item => item.Key == "UserExecutionPilotNotionalLimitExceeded" && item.Count == 1);
    }

    [Fact]
    public async Task CaptureAsync_RejectsUnsupportedFinalAction()
    {
        await using var dbContext = CreateDbContext();
        var service = new AiShadowDecisionService(dbContext, TimeProvider.System);
        var botId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        var nowUtc = new DateTime(2026, 4, 6, 12, 15, 0, DateTimeKind.Utc);

        var action = () => service.CaptureAsync(
            CreateRequest(
                id: Guid.Parse("34343434-3434-3434-3434-343434343434"),
                botId: botId,
                featureSnapshotId: Guid.NewGuid(),
                evaluatedAtUtc: nowUtc,
                finalAction: "UnexpectedAction",
                hypotheticalSubmitAllowed: false,
                hypotheticalBlockReason: "TradeMasterDisarmed",
                noSubmitReason: "TradeMasterDisarmed",
                aiDirection: "Neutral",
                aiConfidence: 0m,
                aiIsFallback: true,
                aiFallbackReason: nameof(AiSignalFallbackReason.ProviderUnavailable),
                riskVetoPresent: false,
                pilotSafetyBlocked: false,
                agreementState: "NotApplicable"));

        await Assert.ThrowsAsync<ArgumentException>(action);
    }

    [Fact]
    public async Task CaptureAsync_PersistsStrategyAiRiskAndPilotComparisonFields_InSameRecord()
    {
        await using var dbContext = CreateDbContext();
        var service = new AiShadowDecisionService(dbContext, TimeProvider.System);
        var botId = Guid.Parse("56565656-5656-5656-5656-565656565656");
        var nowUtc = new DateTime(2026, 4, 6, 12, 30, 0, DateTimeKind.Utc);

        var persisted = await service.CaptureAsync(
            CreateRequest(
                id: Guid.Parse("78787878-7878-7878-7878-787878787878"),
                botId: botId,
                featureSnapshotId: Guid.NewGuid(),
                evaluatedAtUtc: nowUtc,
                finalAction: "NoSubmit",
                hypotheticalSubmitAllowed: false,
                hypotheticalBlockReason: "UserExecutionPilotNotionalLimitExceeded",
                noSubmitReason: "UserExecutionPilotNotionalLimitExceeded",
                aiDirection: "Short",
                aiConfidence: 0.41m,
                aiIsFallback: false,
                aiFallbackReason: null,
                riskVetoPresent: true,
                pilotSafetyBlocked: true,
                agreementState: "Disagreement"));

        Assert.Equal("Long", persisted.StrategyDirection);
        Assert.Equal("Short", persisted.AiDirection);
        Assert.True(persisted.RiskVetoPresent);
        Assert.Equal("MaxDailyLossBreached", persisted.RiskVetoReason);
        Assert.True(persisted.PilotSafetyBlocked);
        Assert.Equal("UserExecutionPilotNotionalLimitExceeded", persisted.PilotSafetyReason);
        Assert.Equal("Disagreement", persisted.AgreementState);
    }

    [Theory]
    [InlineData("Long", 100, 102, true, false)]
    [InlineData("Long", 100, 98, false, true)]
    [InlineData("Short", 100, 98, true, false)]
    [InlineData("Short", 100, 102, false, true)]
    public async Task ScoreOutcomeAsync_ScoresDirectionalDecisionsDeterministically(
        string direction,
        decimal referencePrice,
        decimal futurePrice,
        bool expectPositiveScore,
        bool expectFalsePositive)
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 14, 0, 0, DateTimeKind.Utc);
        var service = new AiShadowDecisionService(dbContext, new FixedTimeProvider(nowUtc.AddMinutes(5)));
        var decision = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: Guid.Parse("89898989-8989-8989-8989-898989898989"),
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc,
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: direction,
            aiConfidence: 0.80m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        SeedHistoricalCandles(dbContext, nowUtc, [referencePrice - 1m, referencePrice, futurePrice]);
        await dbContext.SaveChangesAsync();

        var outcome = await service.ScoreOutcomeAsync("shadow-user", decision.Id);

        Assert.Equal(AiShadowOutcomeState.Scored, outcome.OutcomeState);
        Assert.Equal(AiShadowFutureDataAvailability.Available, outcome.FutureDataAvailability);
        Assert.Equal("High", outcome.ConfidenceBucket);
        Assert.Equal(expectFalsePositive, outcome.FalsePositive);
        Assert.Equal(expectPositiveScore, (outcome.OutcomeScore ?? 0m) > 0m);
        Assert.Equal(direction == "Long" ? (futurePrice > referencePrice ? "Long" : "Short") : (futurePrice < referencePrice ? "Short" : "Long"), outcome.RealizedDirectionality);
    }

    [Fact]
    public async Task ScoreOutcomeAsync_ScoresNeutralDecisionAgainstFlatMove()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 15, 0, 0, DateTimeKind.Utc);
        var service = new AiShadowDecisionService(dbContext, new FixedTimeProvider(nowUtc.AddMinutes(5)));
        var decision = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: Guid.Parse("91919191-9191-9191-9191-919191919191"),
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc,
            finalAction: "NoSubmit",
            hypotheticalSubmitAllowed: false,
            hypotheticalBlockReason: "ShadowHold",
            noSubmitReason: "ShadowHold",
            aiDirection: "Neutral",
            aiConfidence: 0.45m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        SeedHistoricalCandles(dbContext, nowUtc, [99.9m, 100m, 100.05m]);
        await dbContext.SaveChangesAsync();

        var outcome = await service.ScoreOutcomeAsync("shadow-user", decision.Id);

        Assert.Equal(AiShadowOutcomeState.Scored, outcome.OutcomeState);
        Assert.Equal("Neutral", outcome.RealizedDirectionality);
        Assert.True((outcome.OutcomeScore ?? 0m) > 0m);
        Assert.False(outcome.FalseNeutral);
        Assert.True(outcome.SuppressionCandidate);
        Assert.True(outcome.SuppressionAligned);
    }

    [Fact]
    public async Task ScoreOutcomeAsync_ScoresNeutralDecisionAgainstStrongMove_AsFalseNeutral()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 15, 30, 0, DateTimeKind.Utc);
        var service = new AiShadowDecisionService(dbContext, new FixedTimeProvider(nowUtc.AddMinutes(5)));
        var decision = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: Guid.Parse("92929292-9292-9292-9292-929292929292"),
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc,
            finalAction: "NoSubmit",
            hypotheticalSubmitAllowed: false,
            hypotheticalBlockReason: "ShadowHold",
            noSubmitReason: "ShadowHold",
            aiDirection: "Neutral",
            aiConfidence: 0.32m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        SeedHistoricalCandles(dbContext, nowUtc, [99m, 100m, 101m]);
        await dbContext.SaveChangesAsync();

        var outcome = await service.ScoreOutcomeAsync("shadow-user", decision.Id);

        Assert.Equal(AiShadowOutcomeState.Scored, outcome.OutcomeState);
        Assert.True((outcome.OutcomeScore ?? 0m) < 0m);
        Assert.True(outcome.FalseNeutral);
        Assert.True(outcome.SuppressionCandidate);
        Assert.False(outcome.SuppressionAligned);
    }

    [Fact]
    public async Task ScoreOutcomeAsync_UsesExplicitHorizonValue()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 16, 0, 0, DateTimeKind.Utc);
        var service = new AiShadowDecisionService(dbContext, new FixedTimeProvider(nowUtc.AddMinutes(5)));
        var decision = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: Guid.Parse("93939393-9393-9393-9393-939393939393"),
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc,
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Long",
            aiConfidence: 0.70m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        SeedHistoricalCandlesFromStart(dbContext, nowUtc.AddMinutes(-1), [100m, 101m, 100.5m, 104m]);
        await dbContext.SaveChangesAsync();

        var outcome = await service.ScoreOutcomeAsync("shadow-user", decision.Id, AiShadowOutcomeHorizonKind.BarsForward, 2);

        Assert.Equal(2, outcome.HorizonValue);
        Assert.Equal(new DateTime(2026, 4, 6, 16, 2, 0, DateTimeKind.Utc), outcome.FutureCandleCloseTimeUtc);
        Assert.True((outcome.OutcomeScore ?? 0m) > 0m);
    }

    [Fact]
    public async Task ScoreOutcomeAsync_ReturnsFutureDataUnavailable_WhenFutureCandleIsMissing()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 16, 30, 0, DateTimeKind.Utc);
        var service = new AiShadowDecisionService(dbContext, new FixedTimeProvider(nowUtc.AddMinutes(5)));
        var decision = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: Guid.Parse("94949494-9494-9494-9494-949494949494"),
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc,
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Long",
            aiConfidence: 0.61m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        SeedHistoricalCandlesFromStart(dbContext, nowUtc, [101m]);
        await dbContext.SaveChangesAsync();

        var outcome = await service.ScoreOutcomeAsync("shadow-user", decision.Id);

        Assert.Equal(AiShadowOutcomeState.FutureDataUnavailable, outcome.OutcomeState);
        Assert.Equal(AiShadowFutureDataAvailability.MissingFutureCandle, outcome.FutureDataAvailability);
        Assert.Null(outcome.OutcomeScore);
    }

    [Fact]
    public async Task ScoreOutcomeAsync_ReturnsReferenceDataUnavailable_WhenReferenceCandleIsMissing()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 17, 0, 0, DateTimeKind.Utc);
        var service = new AiShadowDecisionService(dbContext, new FixedTimeProvider(nowUtc.AddMinutes(5)));
        var decision = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: Guid.Parse("95959595-9595-9595-9595-959595959595"),
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc,
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Long",
            aiConfidence: 0.61m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));

        var outcome = await service.ScoreOutcomeAsync("shadow-user", decision.Id);

        Assert.Equal(AiShadowOutcomeState.ReferenceDataUnavailable, outcome.OutcomeState);
        Assert.Equal(AiShadowFutureDataAvailability.MissingReferenceCandle, outcome.FutureDataAvailability);
        Assert.Null(outcome.OutcomeScore);
    }

    [Fact]
    public async Task EnsureOutcomeCoverageAsync_IsIdempotent_AndPreventsDuplicateScoring()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 17, 30, 0, DateTimeKind.Utc);
        var service = new AiShadowDecisionService(dbContext, new FixedTimeProvider(nowUtc.AddMinutes(5)));
        var decision = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: Guid.Parse("96969696-9696-9696-9696-969696969696"),
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc,
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Short",
            aiConfidence: 0.51m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        SeedHistoricalCandles(dbContext, nowUtc, [101m, 100m, 99m]);
        await dbContext.SaveChangesAsync();

        var changedFirst = await service.EnsureOutcomeCoverageAsync("shadow-user");
        var changedSecond = await service.EnsureOutcomeCoverageAsync("shadow-user");
        var direct = await service.ScoreOutcomeAsync("shadow-user", decision.Id);
        var outcomes = await dbContext.AiShadowDecisionOutcomes.Where(entity => entity.AiShadowDecisionId == decision.Id).ToListAsync();

        Assert.Equal(1, changedFirst);
        Assert.Equal(0, changedSecond);
        Assert.Single(outcomes);
        Assert.Equal(outcomes[0].Id, direct.Id);
        Assert.True((direct.OutcomeScore ?? 0m) > 0m);
    }

    [Fact]
    public async Task EnsureOutcomeCoverageAsync_ScoresOnlyEligibleRecentDecisions_Bounded()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 18, 10, 0, DateTimeKind.Utc);
        var service = new AiShadowDecisionService(dbContext, new FixedTimeProvider(nowUtc));
        var oldestDecision = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc.AddMinutes(-10),
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Long",
            aiConfidence: 0.72m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        var eligibleDecision = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"),
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc.AddMinutes(-5),
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Long",
            aiConfidence: 0.78m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        var immatureDecision = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: Guid.Parse("cccccccc-dddd-eeee-ffff-111111111111"),
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc,
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Long",
            aiConfidence: 0.81m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        SeedHistoricalCandles(dbContext, eligibleDecision.MarketDataTimestampUtc ?? eligibleDecision.EvaluatedAtUtc, [100m, 101m]);
        SeedHistoricalCandles(dbContext, oldestDecision.MarketDataTimestampUtc ?? oldestDecision.EvaluatedAtUtc, [99m, 100m]);
        await dbContext.SaveChangesAsync();

        var changedCount = await service.EnsureOutcomeCoverageAsync("shadow-user", take: 2);
        var outcomes = await dbContext.AiShadowDecisionOutcomes
            .OrderBy(entity => entity.DecisionEvaluatedAtUtc)
            .ToListAsync();

        Assert.Equal(1, changedCount);
        var outcome = Assert.Single(outcomes);
        Assert.Equal(eligibleDecision.Id, outcome.AiShadowDecisionId);
        Assert.DoesNotContain(outcomes, entity => entity.AiShadowDecisionId == oldestDecision.Id);
        Assert.DoesNotContain(outcomes, entity => entity.AiShadowDecisionId == immatureDecision.Id);
    }

    [Fact]
    public async Task EnsureOutcomeCoverageAsync_DoesNotBreak_WhenFutureDataUnavailable()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 18, 20, 0, DateTimeKind.Utc);
        var service = new AiShadowDecisionService(dbContext, new FixedTimeProvider(nowUtc));
        var decision = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: Guid.Parse("dddddddd-eeee-ffff-1111-222222222222"),
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc.AddMinutes(-5),
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Short",
            aiConfidence: 0.66m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        SeedHistoricalCandlesFromStart(dbContext, decision.MarketDataTimestampUtc ?? decision.EvaluatedAtUtc, [101m]);
        await dbContext.SaveChangesAsync();

        var changedCount = await service.EnsureOutcomeCoverageAsync("shadow-user");
        var outcome = await dbContext.AiShadowDecisionOutcomes.SingleAsync();

        Assert.Equal(1, changedCount);
        Assert.Equal(decision.Id, outcome.AiShadowDecisionId);
        Assert.Equal(AiShadowOutcomeState.FutureDataUnavailable, outcome.OutcomeState);
        Assert.Equal(AiShadowFutureDataAvailability.MissingFutureCandle, outcome.FutureDataAvailability);
        Assert.Null(outcome.OutcomeScore);
    }

    [Fact]
    public async Task EnsureOutcomeCoverageAsync_DoesNotCreateOutcome_WhenNoShadowDecisionExists()
    {
        await using var dbContext = CreateDbContext();
        var service = new AiShadowDecisionService(dbContext, new FixedTimeProvider(new DateTime(2026, 4, 6, 18, 30, 0, DateTimeKind.Utc)));

        var changedCount = await service.EnsureOutcomeCoverageAsync("shadow-user");

        Assert.Equal(0, changedCount);
        Assert.Empty(dbContext.AiShadowDecisions);
        Assert.Empty(dbContext.AiShadowDecisionOutcomes);
    }

    [Fact]
    public async Task GetOutcomeSummaryAsync_AggregatesConfidenceBucketsAndSuppressionMetrics()
    {
        await using var dbContext = CreateDbContext();
        var nowUtc = new DateTime(2026, 4, 6, 18, 0, 0, DateTimeKind.Utc);
        var service = new AiShadowDecisionService(dbContext, new FixedTimeProvider(nowUtc.AddMinutes(5)));
        var botId = Guid.Parse("97979797-9797-9797-9797-979797979797");

        var longShadowOnly = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: botId,
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc,
            finalAction: "ShadowOnly",
            hypotheticalSubmitAllowed: true,
            hypotheticalBlockReason: null,
            noSubmitReason: "ShadowModeActive",
            aiDirection: "Long",
            aiConfidence: 0.80m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        var neutralSuppressed = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: botId,
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc.AddMinutes(10),
            finalAction: "NoSubmit",
            hypotheticalSubmitAllowed: false,
            hypotheticalBlockReason: "AiNeutral",
            noSubmitReason: "AiNeutral",
            aiDirection: "Neutral",
            aiConfidence: 0.50m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: false,
            pilotSafetyBlocked: false,
            agreementState: "Agreement"));
        var longSuppressedButShouldHaveTraded = await service.CaptureAsync(CreateRequest(
            id: Guid.NewGuid(),
            botId: botId,
            featureSnapshotId: Guid.NewGuid(),
            evaluatedAtUtc: nowUtc.AddMinutes(20),
            finalAction: "NoSubmit",
            hypotheticalSubmitAllowed: false,
            hypotheticalBlockReason: "RiskVeto",
            noSubmitReason: "RiskVeto",
            aiDirection: "Long",
            aiConfidence: 0.20m,
            aiIsFallback: false,
            aiFallbackReason: null,
            riskVetoPresent: true,
            pilotSafetyBlocked: false,
            agreementState: "Disagreement"));
        SeedHistoricalCandles(dbContext, nowUtc, [99m, 100m, 101m]);
        SeedHistoricalCandles(dbContext, nowUtc.AddMinutes(10), [100m, 100m, 100.04m]);
        SeedHistoricalCandles(dbContext, nowUtc.AddMinutes(20), [100m, 100m, 102m]);
        await dbContext.SaveChangesAsync();

        await service.ScoreOutcomeAsync("shadow-user", longShadowOnly.Id);
        await service.ScoreOutcomeAsync("shadow-user", neutralSuppressed.Id);
        await service.ScoreOutcomeAsync("shadow-user", longSuppressedButShouldHaveTraded.Id);

        var summary = await service.GetOutcomeSummaryAsync("shadow-user");

        Assert.Equal(3, summary.TotalDecisionCount);
        Assert.Equal(3, summary.ScoredCount);
        Assert.Equal(3, summary.PositiveOutcomeCount);
        Assert.Equal(1, summary.NeutralOutcomeCount);
        Assert.Equal(0, summary.NegativeOutcomeCount);
        Assert.Equal(0, summary.FalsePositiveCount);
        Assert.Equal(0, summary.FalseNeutralCount);
        Assert.Equal(0, summary.OvertradingCount);
        Assert.Equal(1, summary.SuppressionAlignedCount);
        Assert.Equal(1, summary.SuppressionMissedCount);
        Assert.Contains(summary.ConfidenceBuckets, bucket => bucket.Bucket == "High" && bucket.TotalCount == 1 && bucket.SuccessCount == 1);
        Assert.Contains(summary.ConfidenceBuckets, bucket => bucket.Bucket == "Medium" && bucket.TotalCount == 1 && bucket.SuccessCount == 1);
        Assert.Contains(summary.ConfidenceBuckets, bucket => bucket.Bucket == "Low" && bucket.TotalCount == 1 && bucket.SuccessCount == 1);
        Assert.Contains(summary.OutcomeStates, bucket => bucket.Key == nameof(AiShadowOutcomeState.Scored) && bucket.Count == 3);
        Assert.Contains(summary.FutureDataAvailabilityBuckets, bucket => bucket.Key == nameof(AiShadowFutureDataAvailability.Available) && bucket.Count == 3);
    }

    private static void SeedHistoricalCandles(ApplicationDbContext dbContext, DateTime referenceCloseTimeUtc, decimal[] closePrices)
    {
        var startCloseTimeUtc = referenceCloseTimeUtc.AddMinutes(-(closePrices.Length - 2));
        for (var index = 0; index < closePrices.Length; index++)
        {
            var closeTimeUtc = startCloseTimeUtc.AddMinutes(index);
            var closePrice = closePrices[index];
            dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = "BTCUSDT",
                Interval = "1m",
                OpenTimeUtc = closeTimeUtc.AddMinutes(-1),
                CloseTimeUtc = closeTimeUtc,
                OpenPrice = closePrice - 0.2m,
                HighPrice = closePrice + 0.3m,
                LowPrice = closePrice - 0.4m,
                ClosePrice = closePrice,
                Volume = 1000m + (index * 10m),
                ReceivedAtUtc = closeTimeUtc,
                Source = "unit-test"
            });
        }
    }


    private static void SeedHistoricalCandlesFromStart(ApplicationDbContext dbContext, DateTime firstCloseTimeUtc, decimal[] closePrices)
    {
        for (var index = 0; index < closePrices.Length; index++)
        {
            var closeTimeUtc = firstCloseTimeUtc.AddMinutes(index);
            var closePrice = closePrices[index];
            dbContext.HistoricalMarketCandles.Add(new HistoricalMarketCandle
            {
                Id = Guid.NewGuid(),
                Symbol = "BTCUSDT",
                Interval = "1m",
                OpenTimeUtc = closeTimeUtc.AddMinutes(-1),
                CloseTimeUtc = closeTimeUtc,
                OpenPrice = closePrice - 0.2m,
                HighPrice = closePrice + 0.3m,
                LowPrice = closePrice - 0.4m,
                ClosePrice = closePrice,
                Volume = 1000m + (index * 10m),
                ReceivedAtUtc = closeTimeUtc,
                Source = "unit-test"
            });
        }
    }
    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static AiShadowDecisionWriteRequest CreateRequest(
        Guid id,
        Guid botId,
        Guid? featureSnapshotId,
        DateTime evaluatedAtUtc,
        string finalAction,
        bool hypotheticalSubmitAllowed,
        string? hypotheticalBlockReason,
        string noSubmitReason,
        string aiDirection,
        decimal aiConfidence,
        bool aiIsFallback,
        string? aiFallbackReason,
        bool riskVetoPresent,
        bool pilotSafetyBlocked,
        string agreementState)
    {
        return new AiShadowDecisionWriteRequest(
            id,
            "shadow-user",
            botId,
            ExchangeAccountId: Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            TradingStrategyId: Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            TradingStrategyVersionId: Guid.Parse("11111111-2222-3333-4444-555555555555"),
            StrategySignalId: null,
            StrategySignalVetoId: null,
            FeatureSnapshotId: featureSnapshotId,
            StrategyDecisionTraceId: Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"),
            HypotheticalDecisionTraceId: null,
            CorrelationId: "shadow-corr",
            StrategyKey: "shadow-core",
            Symbol: "BTCUSDT",
            Timeframe: "1m",
            EvaluatedAtUtc: evaluatedAtUtc,
            MarketDataTimestampUtc: evaluatedAtUtc,
            FeatureVersion: "AI-1.v1",
            StrategyDirection: "Long",
            StrategyConfidenceScore: 82,
            StrategyDecisionOutcome: "Persisted",
            StrategyDecisionCode: "StrategyEntry",
            StrategySummary: "Strategy favored a long entry.",
            AiDirection: aiDirection,
            AiConfidence: aiConfidence,
            AiReasonSummary: "AI shadow reason.",
            AiProviderName: "DeterministicStub",
            AiProviderModel: "deterministic-v1",
            AiLatencyMs: 17,
            AiIsFallback: aiIsFallback,
            AiFallbackReason: aiFallbackReason,
            RiskVetoPresent: riskVetoPresent,
            RiskVetoReason: riskVetoPresent ? "MaxDailyLossBreached" : null,
            RiskVetoSummary: riskVetoPresent ? "Risk blocked the hypothetical submit." : null,
            PilotSafetyBlocked: pilotSafetyBlocked,
            PilotSafetyReason: pilotSafetyBlocked ? "UserExecutionPilotNotionalLimitExceeded" : null,
            PilotSafetySummary: pilotSafetyBlocked ? "Pilot limit blocked the hypothetical submit." : null,
            TradingMode: ExecutionEnvironment.Live,
            Plane: ExchangeDataPlane.Futures,
            FinalAction: finalAction,
            HypotheticalSubmitAllowed: hypotheticalSubmitAllowed,
            HypotheticalBlockReason: hypotheticalBlockReason,
            HypotheticalBlockSummary: hypotheticalBlockReason is null ? null : "Hypothetical block summary.",
            NoSubmitReason: noSubmitReason,
            FeatureSummary: "Feature summary.",
            AgreementState: agreementState);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        private readonly DateTimeOffset value = new(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
        public override DateTimeOffset GetUtcNow() => value;
    }
}


