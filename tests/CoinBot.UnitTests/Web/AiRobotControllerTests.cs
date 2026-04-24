using System.Security.Claims;
using CoinBot.Application.Abstractions.Dashboard;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Settings;
using CoinBot.Domain.Enums;
using CoinBot.Web.Controllers;
using CoinBot.Web.ViewModels.AiRobot;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.UnitTests.Web;

public sealed class AiRobotControllerTests
{
    [Fact]
    public async Task Index_MapsRealShadowHistoryIntoViewModel()
    {
        var controller = new AiRobotController(
            new FakeUserDashboardLiveReadModelService(),
            new FakeUserSettingsService());
        controller.ControllerContext = CreateControllerContext("user-01");

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AiRobotViewModel>(viewResult.Model);

        Assert.Equal(1, model.Summary.TotalCount);
        Assert.Equal("ShadowOnly", model.Summary.LatestNoTradeStatus);
        Assert.Equal("Armed", model.Summary.TradeMasterStatus);
        Assert.Equal("DemoOnly", model.Summary.TradingModeStatus);
        Assert.Equal("ShadowOnly", model.Summary.PilotActivationStatus);
        Assert.Equal("NoReject", model.Summary.LatestRejectStatus);
        Assert.Equal("+1 bar close-to-close", model.Summary.OutcomeHorizonLabel);
        Assert.Equal("1/1", model.Summary.ScoringCoverage);
        Assert.Equal("+0.640", model.Summary.AverageOutcomeScore);
        Assert.Contains("False+ 0", model.Summary.CalibrationSummary, StringComparison.Ordinal);
        Assert.Single(model.Decisions);
        Assert.Equal("BTCUSDT", model.Decisions[0].Symbol);
        Assert.Equal("Long", model.Decisions[0].AiDirection);
        Assert.Equal("Agreement", model.Decisions[0].Agreement);
        Assert.Equal("ShadowModeActive", model.Decisions[0].NoSubmitReason);
        Assert.Equal("DeterministicStub / stub-v1", model.Decisions[0].Provider);
        Assert.Equal("AI liked momentum confirmation.", model.Decisions[0].ReasonSummary);
        Assert.Equal("StrategyEntry · Persisted · Strategy favored long.", model.Decisions[0].StrategySummary);
        Assert.Equal("Overlay=Boost · Boost=5", model.Decisions[0].OverlaySummary);
        Assert.Contains("Final=ShadowOnly", model.Decisions[0].FinalReasonSummary, StringComparison.Ordinal);
        Assert.Equal("Trend aligned.", model.Decisions[0].TopFeatureHints);
        Assert.Equal("+0.420", model.Decisions[0].AdvisoryScore);
        Assert.Equal("TrendEmaStackBullish +0.30 | MacdLineAboveSignal +0.12", model.Decisions[0].ContributionSummary);
        Assert.Equal("CCCCCCCC", model.Decisions[0].FeatureSnapshotReference);
        Assert.Contains("Trending", model.Decisions[0].RegimeSummary, StringComparison.Ordinal);
        Assert.Equal("Scored · +1 bar", model.Decisions[0].OutcomeLabel);
        Assert.Contains("Score=+0.640", model.Decisions[0].OutcomeDetail, StringComparison.Ordinal);
        Assert.Contains("Bucket=High", model.Decisions[0].OutcomeDetail, StringComparison.Ordinal);
        Assert.Single(model.NoSubmitReasons);
        Assert.Single(model.OutcomeStates);
        Assert.Single(model.FutureDataAvailability);
        Assert.Single(model.OutcomeConfidenceBuckets);
        Assert.Equal("High", model.OutcomeConfidenceBuckets[0].Label);
        Assert.Equal("+0.640", model.OutcomeConfidenceBuckets[0].AverageOutcomeScore);
    }

    [Fact]
    public async Task Index_EmitsHonestEmptyState_WhenNoAiHistoryExists()
    {
        var controller = new AiRobotController(
            new EmptyUserDashboardLiveReadModelService(),
            new FakeUserSettingsService());
        controller.ControllerContext = CreateControllerContext("user-01");

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<AiRobotViewModel>(viewResult.Model);
        Assert.Empty(model.Decisions);
        Assert.Empty(model.NoSubmitReasons);
        Assert.Empty(model.OutcomeStates);
        Assert.Empty(model.FutureDataAvailability);
        Assert.Empty(model.OutcomeConfidenceBuckets);
        Assert.Equal("Henüz gerçek AI shadow kaydı yok.", model.Summary.EmptyStateMessage);
        Assert.Equal("NoShadowData", model.Summary.LatestNoTradeStatus);
        Assert.Equal("0/0", model.Summary.ScoringCoverage);
    }

    private static ControllerContext CreateControllerContext(string userId)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId)],
                "TestAuth"));
        return new ControllerContext
        {
            HttpContext = httpContext
        };
    }

    private sealed class FakeUserDashboardLiveReadModelService : IUserDashboardLiveReadModelService
    {
        public Task<UserDashboardLiveSnapshot> GetSnapshotAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UserDashboardLiveSnapshot(
                new UserDashboardLiveControlSnapshot("Armed", "positive", "DemoOnly", "warning", "ShadowOnly", "neutral", "Fresh", "positive", "Market ready.", "Fresh", "positive", "Private ready."),
                new UserDashboardLatestNoTradeSnapshot("ShadowOnly", "info", "ShadowModeActive", "AI shadow kept execution in no-submit mode.", new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc)),
                new UserDashboardLatestRejectSnapshot("NoReject", "neutral", null, "Son reject kaydı yok.", null, null),
                new UserDashboardAiSummarySnapshot(1, 1, 0, 0, 0, 1, 0, 0.81m, 1, 0, 0),
                [
                    new UserDashboardAiHistoryRowSnapshot(
                        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                        "BTCUSDT",
                        "1m",
                        new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc),
                        "Long",
                        81,
                        "Persisted",
                        "StrategyEntry",
                        "Strategy favored long.",
                        "Long",
                        0.81m,
                        "AI liked momentum confirmation.",
                        "DeterministicStub",
                        "stub-v1",
                        false,
                        null,
                        false,
                        null,
                        null,
                        false,
                        null,
                        null,
                        "ShadowOnly",
                        true,
                        null,
                        null,
                        "ShadowModeActive",
                        "Agreement",
                        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                        "AI-1.v1",
                        "Feature summary.",
                        "Trend aligned.",
                        "Trending",
                        "Bullish",
                        "Expanding",
                        ExecutionEnvironment.Demo,
                        ExchangeDataPlane.Futures,
                        AiShadowOutcomeState.Scored,
                        0.64m,
                        "Long",
                        "High",
                        AiShadowFutureDataAvailability.Available,
                        AiShadowOutcomeHorizonKind.BarsForward,
                        1,
                        false,
                        false,
                        false,
                        false,
                        false,
                        "Boost",
                        5,
                        0.42m,
                        "TrendEmaStackBullish +0.30 | MacdLineAboveSignal +0.12")
                ],
                [new UserDashboardReasonBucketSnapshot("ShadowModeActive", 1)],
                [],
                new UserDashboardAiOutcomeSummarySnapshot(
                    "+1 bar close-to-close",
                    1,
                    1,
                    0,
                    0,
                    0.64m,
                    1,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0),
                [new UserDashboardReasonBucketSnapshot(nameof(AiShadowOutcomeState.Scored), 1)],
                [new UserDashboardReasonBucketSnapshot(nameof(AiShadowFutureDataAvailability.Available), 1)],
                [new UserDashboardAiConfidenceBucketSnapshot("High", 1, 1, 1, 0, 0, 0, 0.64m)]));
        }
    }

    private sealed class EmptyUserDashboardLiveReadModelService : IUserDashboardLiveReadModelService
    {
        public Task<UserDashboardLiveSnapshot> GetSnapshotAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UserDashboardLiveSnapshot(
                new UserDashboardLiveControlSnapshot("Armed", "positive", "DemoOnly", "warning", "ShadowOnly", "neutral", "Unknown", "neutral", "Henüz market-data readiness snapshot yok.", "Unknown", "neutral", "Private plane sync snapshot yok."),
                new UserDashboardLatestNoTradeSnapshot("NoShadowData", "neutral", null, "Henüz AI shadow kaydı yok.", null),
                new UserDashboardLatestRejectSnapshot("NoReject", "neutral", null, "Son reject kaydı yok.", null, null),
                new UserDashboardAiSummarySnapshot(0, 0, 0, 0, 0, 0, 0, 0m, 0, 0, 0),
                [],
                [],
                [],
                new UserDashboardAiOutcomeSummarySnapshot(
                    "+1 bar close-to-close",
                    0,
                    0,
                    0,
                    0,
                    0m,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0),
                [],
                [],
                []));
        }
    }

    private sealed class FakeUserSettingsService : IUserSettingsService
    {
        public Task<UserSettingsSnapshot?> GetAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UserSettingsSnapshot?>(new UserSettingsSnapshot(
                "UTC",
                "UTC",
                "UTC",
                [new UserTimeZoneOptionSnapshot("UTC", "UTC")],
                new BinanceTimeSyncSnapshot(
                    new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc),
                    0,
                    10,
                    new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc),
                    "Synchronized",
                    null)));
        }

        public Task<UserSettingsSaveResult> SaveAsync(string userId, UserSettingsSaveCommand command, string actor, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}

