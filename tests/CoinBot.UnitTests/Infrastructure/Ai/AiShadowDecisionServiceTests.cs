using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.DataScope;
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
        var service = new AiShadowDecisionService(dbContext);
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
        var service = new AiShadowDecisionService(dbContext);
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
        var service = new AiShadowDecisionService(dbContext);
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
        var service = new AiShadowDecisionService(dbContext);
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
            MarketDataTimestampUtc: evaluatedAtUtc.AddSeconds(-5),
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
}


