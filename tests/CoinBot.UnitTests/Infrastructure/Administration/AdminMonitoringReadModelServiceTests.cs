using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.IO;

using HealthSnapshotEntity = CoinBot.Domain.Entities.HealthSnapshot;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class AdminMonitoringReadModelServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_MapsRedisLatencyAndClockDriftMetrics_FromReadModel()
    {
        var now = new DateTime(2026, 3, 24, 12, 30, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();

        dbContext.HealthSnapshots.AddRange(
            new HealthSnapshotEntity
            {
                Id = Guid.NewGuid(),
                SnapshotKey = "dependency-health-monitor",
                SentinelName = "DependencyHealthMonitor",
                DisplayName = "Dependency Health Monitor",
                HealthState = MonitoringHealthState.Degraded,
                FreshnessTier = MonitoringFreshnessTier.Hot,
                CircuitBreakerState = CircuitBreakerStateCode.Cooldown,
                LastUpdatedAtUtc = now,
                ObservedAtUtc = now,
                DbLatencyMs = 6,
                RedisLatencyMs = 17,
                Detail = "DbLatencyMs=6; RedisLatencyMs=17; RedisProbe=Failed; RedisEndpoint=127.0.0.1:6379"
            },
            new HealthSnapshotEntity
            {
                Id = Guid.NewGuid(),
                SnapshotKey = "clock-drift-monitor",
                SentinelName = "ClockDriftMonitor",
                DisplayName = "Clock Drift Monitor",
                HealthState = MonitoringHealthState.Critical,
                FreshnessTier = MonitoringFreshnessTier.Hot,
                CircuitBreakerState = CircuitBreakerStateCode.Cooldown,
                LastUpdatedAtUtc = now,
                ObservedAtUtc = now,
                Detail = "ClockDriftMs=5000; Reason=ClockDriftExceeded"
            });

        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        var dependencySnapshot = Assert.Single(snapshot.HealthSnapshots, item => item.SnapshotKey == "dependency-health-monitor");
        var clockDriftSnapshot = Assert.Single(snapshot.HealthSnapshots, item => item.SnapshotKey == "clock-drift-monitor");

        Assert.Equal(17, dependencySnapshot.Metrics.RedisLatencyMs);
        Assert.Equal(5000, clockDriftSnapshot.Metrics.ClockDriftMs);
    }

    [Fact]
    public async Task GetSnapshotAsync_ProjectsLatestMarketScannerCycleAndCandidates()
    {
        var now = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Unspecified);
        var cycleId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = cycleId,
            StartedAtUtc = now.AddSeconds(-2),
            CompletedAtUtc = now,
            UniverseSource = "config+registry",
            ScannedSymbolCount = 3,
            EligibleCandidateCount = 2,
            TopCandidateCount = 2,
            BestCandidateSymbol = "BTCUSDT",
            BestCandidateScore = 123456m,
            Summary = "scan complete"
        });
        dbContext.MarketScannerCandidates.AddRange(
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = cycleId,
                Symbol = "BTCUSDT",
                UniverseSource = "config+registry",
                ObservedAtUtc = now,
                LastCandleAtUtc = now,
                LastPrice = 100m,
                QuoteVolume24h = 123456m,
                IsEligible = true,
                RejectionReason = null,
                Score = 123456m,
                Rank = 1,
                IsTopCandidate = true
            },
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = cycleId,
                Symbol = "DOGEUSDT",
                UniverseSource = "config",
                ObservedAtUtc = now,
                LastCandleAtUtc = null,
                LastPrice = null,
                QuoteVolume24h = null,
                IsEligible = false,
                RejectionReason = "MissingMarketData",
                Score = 0m,
                Rank = null,
                IsTopCandidate = false
            });

        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(cycleId, snapshot.MarketScanner.ScanCycleId);
        Assert.Equal(DateTime.SpecifyKind(now, DateTimeKind.Utc), snapshot.MarketScanner.LastScanCompletedAtUtc);
        Assert.Equal(DateTimeKind.Utc, snapshot.MarketScanner.LastScanCompletedAtUtc!.Value.Kind);
        Assert.Equal(3, snapshot.MarketScanner.ScannedSymbolCount);
        Assert.Equal(2, snapshot.MarketScanner.EligibleCandidateCount);
        Assert.Equal("config+registry", snapshot.MarketScanner.UniverseSource);
        Assert.Equal("BTCUSDT", snapshot.MarketScanner.BestCandidateSymbol);
        Assert.Equal(123456m, snapshot.MarketScanner.BestCandidateScore);

        var topCandidate = Assert.Single(snapshot.MarketScanner.TopCandidates);
        Assert.Equal("BTCUSDT", topCandidate.Symbol);
        Assert.Equal(1, topCandidate.Rank);
        Assert.True(topCandidate.IsTopCandidate);
        Assert.Equal(DateTimeKind.Utc, topCandidate.ObservedAtUtc.Kind);
        Assert.Empty(topCandidate.AdvisoryLabels);
        Assert.Empty(topCandidate.AdvisoryReasonCodes);
        Assert.Null(topCandidate.AdvisorySummary);
        Assert.Null(topCandidate.AdvisoryShadowScore);
        Assert.Empty(topCandidate.AdvisoryShadowContributions);

        var rejectedSample = Assert.Single(snapshot.MarketScanner.RejectedSamples);
        Assert.Equal("DOGEUSDT", rejectedSample.Symbol);
        Assert.Equal("MissingMarketData", rejectedSample.RejectionReason);
    }

    [Fact]
    public async Task GetSnapshotAsync_ParsesScannerAdvisoryLabels_FromScoringSummary()
    {
        var now = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
        var cycleId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = cycleId,
            StartedAtUtc = now.AddSeconds(-2),
            CompletedAtUtc = now,
            UniverseSource = "config+registry",
            ScannedSymbolCount = 1,
            EligibleCandidateCount = 1,
            TopCandidateCount = 1,
            BestCandidateSymbol = "BTCUSDT",
            BestCandidateScore = 95m,
            Summary = "scan complete"
        });
        dbContext.MarketScannerCandidates.Add(new MarketScannerCandidate
        {
            Id = Guid.NewGuid(),
            ScanCycleId = cycleId,
            Symbol = "BTCUSDT",
            UniverseSource = "config+registry",
            ObservedAtUtc = now,
            LastCandleAtUtc = now,
            LastPrice = 100m,
            QuoteVolume24h = 123456m,
            MarketScore = 100m,
            StrategyScore = 95,
            ScoringSummary = "StrategyScore=95; ScannerLabels=HasCompressionBreakoutSetup,HasTrendBreakoutUp; ScannerReasonCodes=CompressionBreakoutSetupDetected,TrendBreakoutConfirmed; ScannerReasonSummary=Compression breakout setup detected from tight Bollinger bandwidth. | Bullish trend breakout confirmed above the Bollinger mid-band with positive MACD alignment.; ScannerShadowScore=80; ScannerShadowContributions=CompressionBreakoutSetupDetected:+25,TrendBreakoutConfirmed:+55",
            IsEligible = true,
            Score = 95m,
            Rank = 1,
            IsTopCandidate = true
        });
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        var topCandidate = Assert.Single(snapshot.MarketScanner.TopCandidates);
        Assert.Equal(["HasCompressionBreakoutSetup", "HasTrendBreakoutUp"], topCandidate.AdvisoryLabels.ToArray());
        Assert.Equal(["CompressionBreakoutSetupDetected", "TrendBreakoutConfirmed"], topCandidate.AdvisoryReasonCodes.ToArray());
        Assert.Contains("Bullish trend breakout confirmed", topCandidate.AdvisorySummary, StringComparison.Ordinal);
        Assert.Equal(80, topCandidate.AdvisoryShadowScore);
        Assert.Equal(["CompressionBreakoutSetupDetected +25", "TrendBreakoutConfirmed +55"], topCandidate.AdvisoryShadowContributions.ToArray());
    }

    [Fact]
    public async Task GetSnapshotAsync_ParsesRejectedSampleAdvisoryReasonCodes_AndDistinctsDuplicates()
    {
        var now = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
        var cycleId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = cycleId,
            StartedAtUtc = now.AddSeconds(-2),
            CompletedAtUtc = now,
            UniverseSource = "config+registry",
            ScannedSymbolCount = 1,
            EligibleCandidateCount = 0,
            TopCandidateCount = 0,
            Summary = "scan complete"
        });
        dbContext.MarketScannerCandidates.Add(new MarketScannerCandidate
        {
            Id = Guid.NewGuid(),
            ScanCycleId = cycleId,
            Symbol = "BTCUSDT",
            UniverseSource = "config+registry",
            ObservedAtUtc = now,
            LastCandleAtUtc = now,
            LastPrice = 100m,
            QuoteVolume24h = 123456m,
            MarketScore = 100m,
            StrategyScore = 95,
            ScoringSummary = "StrategyScore=95; ScannerLabels=HasTrendBreakoutUp,HasTrendBreakoutUp; ScannerReasonCodes=TrendBreakoutConfirmed,TrendBreakoutConfirmed,CompressionBreakoutSetupDetected; ScannerReasonSummary=Bullish trend breakout confirmed above the Bollinger mid-band with positive MACD alignment.; ScannerShadowScore=80; ScannerShadowContributions=TrendBreakoutConfirmed:+55,TrendBreakoutConfirmed:+55,CompressionBreakoutSetupDetected:+25; HistoricalRecoveryApplied=True; HistoricalRecoverySource=Binance.Rest.KlineRecovery",
            IsEligible = false,
            RejectionReason = "NoEnabledBotForSymbol",
            Score = 0m,
            Rank = null,
            IsTopCandidate = false
        });
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        var rejectedSample = Assert.Single(snapshot.MarketScanner.RejectedSamples);
        Assert.Equal(["HasTrendBreakoutUp"], rejectedSample.AdvisoryLabels.ToArray());
        Assert.Equal(["CompressionBreakoutSetupDetected", "TrendBreakoutConfirmed"], rejectedSample.AdvisoryReasonCodes.ToArray());
        Assert.Contains("Bullish trend breakout confirmed", rejectedSample.AdvisorySummary, StringComparison.Ordinal);
        Assert.Equal(80, rejectedSample.AdvisoryShadowScore);
        Assert.Equal(["TrendBreakoutConfirmed +55", "CompressionBreakoutSetupDetected +25"], rejectedSample.AdvisoryShadowContributions.ToArray());
    }

    [Fact]
    public void MarketScannerCardView_RendersReasonCodes_AndDoesNotRenderMachineLabels()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "Foundation",
            "_AdminMarketScannerCard.cshtml"));

        Assert.Contains("candidate.AdvisoryReasonCodes", content, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate.AdvisoryLabels", content, StringComparison.Ordinal);
        Assert.Contains("candidate.AdvisoryShadowScore", content, StringComparison.Ordinal);
        Assert.Contains("candidate.AdvisoryShadowContributions", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-rejected-reason-codes", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-rejected-advisory-summary", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_ExcludesLegacyDirtyMarketScoreCandidates_FromDashboardProjection()
    {
        var now = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
        var cycleId = Guid.NewGuid();
        var dirtyCandidateId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = cycleId,
            StartedAtUtc = now.AddSeconds(-2),
            CompletedAtUtc = now,
            UniverseSource = "config+registry",
            ScannedSymbolCount = 2,
            EligibleCandidateCount = 2,
            TopCandidateCount = 2,
            BestCandidateSymbol = "BTCUSDT",
            BestCandidateScore = 95m,
            Summary = "scan complete"
        });
        dbContext.MarketScannerCandidates.AddRange(
            new MarketScannerCandidate
            {
                Id = dirtyCandidateId,
                ScanCycleId = cycleId,
                Symbol = "BTCUSDT",
                UniverseSource = "config+registry",
                ObservedAtUtc = now,
                LastCandleAtUtc = now,
                LastPrice = 100m,
                QuoteVolume24h = 123456m,
                MarketScore = 123456m,
                IsEligible = true,
                Score = 95m,
                Rank = 1,
                IsTopCandidate = true
            },
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = cycleId,
                Symbol = "ETHUSDT",
                UniverseSource = "config+registry",
                ObservedAtUtc = now,
                LastCandleAtUtc = now,
                LastPrice = 90m,
                QuoteVolume24h = 100000m,
                MarketScore = 100m,
                IsEligible = true,
                Score = 80m,
                Rank = 2,
                IsTopCandidate = true
            });
        dbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = cycleId,
            SelectedCandidateId = dirtyCandidateId,
            SelectedSymbol = "BTCUSDT",
            SelectedTimeframe = "1m",
            SelectedAtUtc = now,
            CandidateRank = 1,
            CandidateMarketScore = 123456m,
            CandidateScore = 95m,
            SelectionReason = "legacy",
            StrategyDecisionOutcome = "Persisted",
            ExecutionRequestStatus = "Prepared",
            CompletedAtUtc = now,
            CorrelationId = "corr-legacy"
        });

        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal("ETHUSDT", snapshot.MarketScanner.BestCandidateSymbol);
        Assert.Equal(80m, snapshot.MarketScanner.BestCandidateScore);
        Assert.Equal(1, snapshot.MarketScanner.ScannedSymbolCount);
        Assert.Equal(1, snapshot.MarketScanner.EligibleCandidateCount);
        var topCandidate = Assert.Single(snapshot.MarketScanner.TopCandidates);
        Assert.Equal("ETHUSDT", topCandidate.Symbol);
        Assert.Null(snapshot.MarketScanner.LatestHandoff.CandidateMarketScore);
    }

    [Fact]
    public async Task GetSnapshotAsync_ExposesNoEligibleCandidateReasonBreakdown()
    {
        var now = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
        var cycleId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = cycleId,
            StartedAtUtc = now.AddSeconds(-2),
            CompletedAtUtc = now,
            UniverseSource = "config+registry",
            ScannedSymbolCount = 3,
            EligibleCandidateCount = 0,
            TopCandidateCount = 0,
            Summary = null
        });
        dbContext.MarketScannerCandidates.AddRange(
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = cycleId,
                Symbol = "BTCUSDT",
                UniverseSource = "config",
                ObservedAtUtc = now,
                IsEligible = false,
                RejectionReason = "QuoteVolume24hMissing",
                Score = 0m
            },
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = cycleId,
                Symbol = "ETHUSDT",
                UniverseSource = "registry",
                ObservedAtUtc = now,
                IsEligible = false,
                RejectionReason = "QuoteVolume24hMissing",
                Score = 0m
            },
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = cycleId,
                Symbol = "SOLUSDT",
                UniverseSource = "registry",
                ObservedAtUtc = now,
                IsEligible = false,
                RejectionReason = "HistoricalParityLag",
                Score = 0m
            });

        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.Contains("found no eligible candidates", snapshot.MarketScanner.CycleSummary ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("QuoteVolume24hMissing:2 [BTCUSDT,ETHUSDT]", snapshot.MarketScanner.RejectionSummary ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("HistoricalParityLag:1 [SOLUSDT]", snapshot.MarketScanner.RejectionSummary ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_ProjectsLatestMarketScannerHandoffSnapshots()
    {
        var now = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
        var cycleId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = cycleId,
            StartedAtUtc = now.AddSeconds(-2),
            CompletedAtUtc = now,
            UniverseSource = "config",
            ScannedSymbolCount = 1,
            EligibleCandidateCount = 1,
            TopCandidateCount = 1,
            BestCandidateSymbol = "BTCUSDT",
            BestCandidateScore = 123456m,
            Summary = "scan complete"
        });
        dbContext.MarketScannerHandoffAttempts.AddRange(
            new MarketScannerHandoffAttempt
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                ScanCycleId = cycleId,
                SelectedCandidateId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                SelectedSymbol = "BTCUSDT",
                SelectedTimeframe = "1m",
                SelectedAtUtc = now.AddSeconds(-1),
                CandidateRank = 1,
                CandidateScore = 123456m,
                SelectionReason = "Top-ranked eligible candidate selected.",
                OwnerUserId = "user-btc",
                BotId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                StrategyKey = "pilot-btc",
                TradingStrategyId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                TradingStrategyVersionId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                StrategySignalId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
                StrategyDecisionOutcome = "Persisted",
                StrategyScore = 91,
                ExecutionRequestStatus = "Prepared",
                ExecutionSide = ExecutionOrderSide.Buy,
                ExecutionOrderType = ExecutionOrderType.Market,
                ExecutionEnvironment = ExecutionEnvironment.Live,
                ExecutionQuantity = 0.01m,
                ExecutionPrice = 100m,
                GuardSummary = "ExecutionGate=Allowed; UserExecutionOverride=Allowed; Symbol=BTCUSDT; Timeframe=1m",
                CorrelationId = "corr-prepared",
                CompletedAtUtc = now.AddSeconds(-1)
            },
            new MarketScannerHandoffAttempt
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                ScanCycleId = cycleId,
                SelectedCandidateId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                SelectedSymbol = "SOLUSDT",
                SelectedTimeframe = "1m",
                SelectedAtUtc = now,
                CandidateRank = 2,
                CandidateScore = 99999m,
                SelectionReason = "Top-ranked eligible candidate selected.",
                OwnerUserId = "user-sol",
                BotId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                StrategyKey = "pilot-sol",
                TradingStrategyId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                TradingStrategyVersionId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                StrategySignalVetoId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                StrategyDecisionOutcome = "Vetoed",
                StrategyVetoReasonCode = "ExposureLimitBreached",
                StrategyScore = 12,
                ExecutionRequestStatus = "Blocked",
                BlockerCode = "StrategyVetoed",
                BlockerDetail = "Exposure limit breached.",
                GuardSummary = "StrategySignalVeto=ExposureLimitBreached; Symbol=SOLUSDT; Timeframe=1m",
                CorrelationId = "corr-blocked",
                CompletedAtUtc = now
            });
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal("SOLUSDT", snapshot.MarketScanner.LatestHandoff.SelectedSymbol);
        Assert.Equal("Blocked", snapshot.MarketScanner.LatestHandoff.ExecutionRequestStatus);
        Assert.Equal("StrategyVetoed", snapshot.MarketScanner.LatestHandoff.BlockerCode);
        Assert.Equal("Vetoed", snapshot.MarketScanner.LatestHandoff.StrategyDecisionOutcome);
        Assert.Equal("Block", snapshot.MarketScanner.LatestHandoff.DecisionOutcome);
        Assert.Equal("RiskVeto", snapshot.MarketScanner.LatestHandoff.DecisionReasonType);
        Assert.Equal("StrategyVetoed", snapshot.MarketScanner.LatestHandoff.DecisionReasonCode);
        Assert.Equal("Exposure limit breached.", snapshot.MarketScanner.LatestHandoff.DecisionSummary);
        Assert.Equal("StrategyVetoed: Exposure limit breached.", snapshot.MarketScanner.LatestHandoff.BlockerSummary);
        Assert.Equal("BTCUSDT", snapshot.MarketScanner.LastSuccessfulHandoff.SelectedSymbol);
        Assert.Equal("Prepared", snapshot.MarketScanner.LastSuccessfulHandoff.ExecutionRequestStatus);
        Assert.Equal("Allow", snapshot.MarketScanner.LastSuccessfulHandoff.DecisionOutcome);
        Assert.Equal("Allowed", snapshot.MarketScanner.LastSuccessfulHandoff.DecisionReasonCode);
        Assert.Equal("Allowed: execution request prepared.", snapshot.MarketScanner.LastSuccessfulHandoff.BlockerSummary);
        Assert.Equal("SOLUSDT", snapshot.MarketScanner.LastBlockedHandoff.SelectedSymbol);
        Assert.Equal("StrategyVetoed", snapshot.MarketScanner.LastBlockedHandoff.BlockerCode);
        Assert.Equal(DateTimeKind.Utc, snapshot.MarketScanner.LatestHandoff.CompletedAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task GetSnapshotAsync_ProjectsContinuityDecisionSurface_FromHandoffAndDegradedModeState()
    {
        var now = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
        var cycleId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = cycleId,
            StartedAtUtc = now.AddSeconds(-30),
            CompletedAtUtc = now,
            UniverseSource = "config",
            ScannedSymbolCount = 1,
            EligibleCandidateCount = 1,
            TopCandidateCount = 1,
            BestCandidateSymbol = "BTCUSDT",
            BestCandidateScore = 10_000m,
            Summary = "continuity"
        });
        dbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = cycleId,
            SelectedSymbol = "BTCUSDT",
            SelectedTimeframe = "1m",
            SelectedAtUtc = now,
            SelectionReason = "Top-ranked eligible candidate selected.",
            StrategyDecisionOutcome = "Persisted",
            ExecutionRequestStatus = "Blocked",
            BlockerCode = "ContinuityGap",
            BlockerDetail = "Execution blocked because the candle continuity guard is active. LatencyReason=CandleDataGapDetected; Symbol=BTCUSDT; Timeframe=1m; LastCandleAtUtc=2026-04-03T11:59:00.0000000Z; DataAgeMs=60000; ContinuityGapCount=2",
            GuardSummary = "ExecutionGate=ContinuityGap; Symbol=BTCUSDT; Timeframe=1m",
            CorrelationId = "corr-continuity",
            CompletedAtUtc = now
        });
        dbContext.DegradedModeStates.Add(new DegradedModeState
        {
            Id = DegradedModeDefaults.ResolveStateId("BTCUSDT", "1m"),
            StateCode = DegradedModeStateCode.Normal,
            ReasonCode = DegradedModeReasonCode.None,
            SignalFlowBlocked = false,
            ExecutionFlowBlocked = false,
            LatestSymbol = "BTCUSDT",
            LatestTimeframe = "1m",
            LatestDataTimestampAtUtc = now.AddMinutes(-1),
            LatestExpectedOpenTimeUtc = now,
            LatestContinuityGapCount = 2,
            LatestContinuityGapStartedAtUtc = now.AddMinutes(-3),
            LatestContinuityGapLastSeenAtUtc = now.AddMinutes(-1),
            LatestContinuityRecoveredAtUtc = now.AddSeconds(-10)
        });
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();
        var handoff = snapshot.MarketScanner.LatestHandoff;

        Assert.Equal("Block", handoff.DecisionOutcome);
        Assert.Equal("ContinuityGap", handoff.DecisionReasonType);
        Assert.Equal("ContinuityGap", handoff.DecisionReasonCode);
        Assert.Equal("Execution blocked because the candle continuity guard is active.", handoff.DecisionSummary);
        Assert.Equal(new DateTime(2026, 4, 3, 11, 59, 0, DateTimeKind.Utc), handoff.MarketDataLastCandleAtUtc);
        Assert.Equal(60000, handoff.MarketDataAgeMilliseconds);
        Assert.Equal(3000, handoff.MarketDataStaleThresholdMilliseconds);
        Assert.Equal("Continuity gap detected", handoff.MarketDataStaleReason);
        Assert.Equal("Recovered after backfill", handoff.ContinuityState);
        Assert.Equal(2, handoff.ContinuityGapCount);
        Assert.Equal(now.AddMinutes(-3), handoff.ContinuityGapStartedAtUtc);
        Assert.Equal(now.AddMinutes(-1), handoff.ContinuityGapLastSeenAtUtc);
        Assert.Equal(now.AddSeconds(-10), handoff.ContinuityRecoveredAtUtc);
    }
    [Fact]
    public async Task GetSnapshotAsync_ProjectsSharedMarketDataCacheHealthSnapshot_FromCollector()
    {
        var now = new DateTime(2026, 4, 3, 21, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();
        var cacheCollector = new SharedMarketDataCacheObservabilityCollector(new FixedTimeProvider(now));
        var tickerEntry = new SharedMarketDataCacheEntry<MarketPriceSnapshot>(
            SharedMarketDataCacheDataType.Ticker,
            "BTCUSDT",
            "spot",
            now.AddSeconds(-1),
            now,
            now.AddSeconds(10),
            now.AddMinutes(1),
            "Binance.WebSocket.Ticker",
            new MarketPriceSnapshot("BTCUSDT", 65000m, now.AddSeconds(-1), now, "Binance.WebSocket.Ticker"));

        cacheCollector.RecordRead(
            SharedMarketDataCacheDataType.Ticker,
            "btcusdt",
            null,
            SharedMarketDataCacheReadResult<MarketPriceSnapshot>.HitFresh(tickerEntry));
        cacheCollector.RecordRead(
            SharedMarketDataCacheDataType.Kline,
            "ethusdt",
            "1m",
            SharedMarketDataCacheReadResult<MarketCandleSnapshot>.ProviderUnavailable("Redis unavailable."));
        cacheCollector.RecordProjection(
            SharedMarketDataCacheDataType.Depth,
            "solusdt",
            null,
            SharedMarketDataProjectionResult.IgnoredOutOfOrder(SharedMarketDataProjectionReasonCode.DepthOutOfOrder, "Older depth."),
            now.AddSeconds(-5),
            now.AddSeconds(5),
            "Binance.WebSocket.Depth");

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()),
            cacheCollector);

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(1, snapshot.MarketDataCache.HitCount);
        Assert.Equal(1, snapshot.MarketDataCache.ProviderUnavailableCount);
        Assert.Equal(0, snapshot.MarketDataCache.MissCount);
        Assert.Equal(DateTime.SpecifyKind(now, DateTimeKind.Utc), snapshot.MarketDataCache.LastObservedAtUtc);

        var tickerScope = Assert.Single(snapshot.MarketDataCache.SymbolFreshness, item => item.DataType == SharedMarketDataCacheDataType.Ticker && item.Symbol == "BTCUSDT");
        Assert.Equal(SharedMarketDataCacheReadStatus.HitFresh, tickerScope.LastReadStatus);
        Assert.Equal(SharedMarketDataCacheStaleReasonCode.Fresh, tickerScope.StaleReasonCode);
        Assert.Equal("Binance.WebSocket.Ticker", tickerScope.SourceLayer);
        Assert.Equal(DateTime.SpecifyKind(now.AddSeconds(-1), DateTimeKind.Utc), tickerScope.UpdatedAtUtc);

        var klineScope = Assert.Single(snapshot.MarketDataCache.SymbolFreshness, item => item.DataType == SharedMarketDataCacheDataType.Kline && item.Symbol == "ETHUSDT");
        Assert.Equal(SharedMarketDataCacheReadStatus.ProviderUnavailable, klineScope.LastReadStatus);
        Assert.Equal(SharedMarketDataCacheStaleReasonCode.ProviderUnavailable, klineScope.StaleReasonCode);

        var depthScope = Assert.Single(snapshot.MarketDataCache.SymbolFreshness, item => item.DataType == SharedMarketDataCacheDataType.Depth && item.Symbol == "SOLUSDT");
        Assert.Equal(SharedMarketDataProjectionStatus.IgnoredOutOfOrder, depthScope.LastProjectionStatus);
        Assert.Equal(SharedMarketDataCacheStaleReasonCode.IgnoredOutOfOrder, depthScope.StaleReasonCode);
        Assert.Equal("Older depth.", depthScope.ReasonSummary);

        var tickerStream = Assert.Single(snapshot.MarketDataCache.StreamSnapshots, item => item.DataType == SharedMarketDataCacheDataType.Ticker);
        Assert.Equal("BTCUSDT", tickerStream.Symbol);
        Assert.Equal("spot", tickerStream.Timeframe);
        Assert.Equal("Binance.WebSocket.Ticker", tickerStream.SourceLayer);
    }


    [Fact]
    public async Task GetSnapshotAsync_UsesSizedCacheEntry_WhenMemoryCacheHasSizeLimit()
    {
        await using var dbContext = CreateDbContext();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 128 });
        var now = new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero);
        var service = new AdminMonitoringReadModelService(
            dbContext,
            memoryCache,
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();
        var cachedSnapshot = await service.GetSnapshotAsync();

        Assert.Equal(snapshot.LastRefreshedAtUtc, cachedSnapshot.LastRefreshedAtUtc);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "CoinBot.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("CoinBot repository root could not be resolved.");
        }

        return directory.FullName;
    }
}
