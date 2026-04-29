using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Jobs;
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
    public async Task AdminScannerReadModel_ShowsAiRankingSummary_WhenPresent()
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
            ScoringSummary = "StrategyScore=95; ScannerRankingMode=AdvisoryCombined; ScannerClassicalScore=96.6667; ScannerCombinedScore=90.8334; ScannerAiInfluenceWeight=0.35; ScannerOutcomeCoveragePercent=75; ScannerLabels=HasCompressionBreakoutSetup,HasTrendBreakoutUp; ScannerReasonCodes=CompressionBreakoutSetupDetected,TrendBreakoutConfirmed; ScannerReasonSummary=Compression breakout setup detected from tight Bollinger bandwidth. | Bullish trend breakout confirmed above the Bollinger mid-band with positive MACD alignment.; ScannerShadowScore=80; ScannerShadowContributions=CompressionBreakoutSetupDetected:+25,TrendBreakoutConfirmed:+55; ScannerAdaptiveFilterState=Passed",
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
        Assert.Equal("AdvisoryCombined", topCandidate.AiRankingMode);
        Assert.Equal(96.6667m, topCandidate.AiRankingClassicalScore);
        Assert.Equal(90.8334m, topCandidate.AiRankingCombinedScore);
        Assert.Equal(0.35m, topCandidate.AiRankingInfluenceWeight);
        Assert.Equal(75m, topCandidate.AiRankingOutcomeCoveragePercent);
        Assert.Null(topCandidate.AiRankingFallbackReason);
        Assert.Equal("Passed", topCandidate.AiRankingAdaptiveFilterState);
    }

    [Fact]
    public async Task GetSnapshotAsync_ProjectsScannerDecisionObservabilitySummary_WithoutRawSqlInspection()
    {
        var now = new DateTime(2026, 4, 29, 14, 0, 0, DateTimeKind.Utc);
        var cycleId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = cycleId,
            StartedAtUtc = now.AddSeconds(-5),
            CompletedAtUtc = now,
            UniverseSource = "config+registry",
            ScannedSymbolCount = 5,
            EligibleCandidateCount = 2,
            TopCandidateCount = 2,
            BestCandidateSymbol = "BTCUSDT",
            BestCandidateScore = 96.6667m,
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
                MarketScore = 100m,
                StrategyScore = 95,
                ScoringSummary = "RankingDecision=Selected; RankingReasonCode=HighestCompositeScore; RankingSummary=SelectedByRanking; CandidateScore=96.6667; MarketScore=100; StrategyScore=95; RiskPenalty=0; VolatilityScore=100; LiquidityScore=100; TrendAlignment=Bullish; DirectionalConflictStatus=NotEvaluated",
                IsEligible = true,
                Score = 96.6667m,
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
                LastPrice = 50m,
                QuoteVolume24h = 654321m,
                MarketScore = 92m,
                StrategyScore = 81,
                ScoringSummary = "RankingDecision=Ranked; RankingReasonCode=LowerCompositeScore; RankingSummary=RankedBelowBest; CandidateScore=90; MarketScore=92; StrategyScore=81; RiskPenalty=0; VolatilityScore=88; LiquidityScore=92; TrendAlignment=Bullish; DirectionalConflictStatus=NotEvaluated",
                IsEligible = true,
                Score = 90m,
                Rank = 2,
                IsTopCandidate = true
            },
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = cycleId,
                Symbol = "XRPUSDT",
                UniverseSource = "config+registry",
                ObservedAtUtc = now,
                IsEligible = false,
                RejectionReason = "NoEnabledBotForSymbol",
                ScoringSummary = "RankingDecision=Rejected; RankingReasonCode=NoEnabledBotForSymbol; RankingSummary=RejectedBeforeRanking; CandidateScore=0; RiskPenalty=10; VolatilityScore=70; LiquidityScore=80; TrendAlignment=Neutral; DirectionalConflictStatus=NotEvaluated"
            });
        dbContext.MarketScannerHandoffAttempts.AddRange(
            CreateBlockedHandoffAttempt(now.AddMinutes(-1), "DirectionalConflictShortAgainstBullishScanner"),
            CreateBlockedHandoffAttempt(now.AddMinutes(-2), "DirectionalConflictShortAgainstBullishScanner"),
            CreateBlockedHandoffAttempt(now.AddMinutes(-3), "SameDirectionLongEntrySuppressed"),
            CreateBlockedHandoffAttempt(now.AddMinutes(-4), "DuplicateExecutionRequestSuppressed"),
            CreateBlockedHandoffAttempt(now.AddMinutes(-5), "SymbolExecutionNotAllowed"),
            CreateBlockedHandoffAttempt(now.AddMinutes(-6), "RiskConcurrencyMaxOpenPositionsExceeded"),
            CreateBlockedHandoffAttempt(now.AddMinutes(-7), "StaleMarketData"));
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();
        var scanner = snapshot.MarketScanner;
        var bestCandidate = Assert.Single(scanner.TopCandidates, item => item.Symbol == "BTCUSDT");
        var rejectedSample = Assert.Single(scanner.RejectedSamples);

        Assert.Equal(2, scanner.TopCandidateCount);
        Assert.Contains("HighestCompositeScore", scanner.BestCandidateRankingSummary, StringComparison.Ordinal);
        Assert.Contains("Volatility 100", scanner.BestCandidateRankingSummary, StringComparison.Ordinal);
        Assert.Contains("DirectionalConflictShortAgainstBullishScanner:2", scanner.DirectionalConflictSummary, StringComparison.Ordinal);
        Assert.Contains("SameDirectionLongEntrySuppressed:1", scanner.SameDirectionSummary, StringComparison.Ordinal);
        Assert.Contains("DuplicateExecutionRequestSuppressed:1", scanner.DuplicateSummary, StringComparison.Ordinal);
        Assert.Contains("SymbolExecutionNotAllowed:1", scanner.GuardrailSummary, StringComparison.Ordinal);
        Assert.Contains("RiskConcurrencyMaxOpenPositionsExceeded:1", scanner.GuardrailSummary, StringComparison.Ordinal);
        Assert.Contains("StaleMarketData:1", scanner.GuardrailSummary, StringComparison.Ordinal);
        Assert.Equal("Selected", bestCandidate.RankingDecision);
        Assert.Equal("HighestCompositeScore", bestCandidate.RankingReasonCode);
        Assert.Equal(100m, bestCandidate.VolatilityScore);
        Assert.Equal("NotEvaluated", bestCandidate.DirectionalConflictStatus);
        Assert.Equal("Rejected", rejectedSample.RankingDecision);
        Assert.Equal("NoEnabledBotForSymbol", rejectedSample.RankingReasonCode);
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
    public void AdminScannerReadModel_DoesNotShowRawUnsafeFields()
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
        Assert.Contains("candidate.AiRankingMode", content, StringComparison.Ordinal);
        Assert.Contains("candidate.AiRankingClassicalScore", content, StringComparison.Ordinal);
        Assert.Contains("candidate.AiRankingCombinedScore", content, StringComparison.Ordinal);
        Assert.Contains("candidate.AiRankingFallbackReason", content, StringComparison.Ordinal);
        Assert.Contains("candidate.AiRankingAdaptiveFilterState", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-ai-status-summary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-ai-empty-state", content, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate.ScoringSummary", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-rejected-reason-codes", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-scanner-rejected-advisory-summary", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_ComputesAiRankingObservabilityCounts_FromLatestCycle()
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
            EligibleCandidateCount = 2,
            TopCandidateCount = 2,
            BestCandidateSymbol = "ETHUSDT",
            BestCandidateScore = 88m,
            Summary = "scan complete"
        });
        dbContext.MarketScannerCandidates.AddRange(
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = cycleId,
                Symbol = "ETHUSDT",
                UniverseSource = "config",
                ObservedAtUtc = now,
                IsEligible = true,
                Score = 88m,
                Rank = 1,
                IsTopCandidate = true,
                ScoringSummary = "ScannerRankingMode=AdvisoryCombined; ScannerClassicalScore=70; ScannerCombinedScore=88; ScannerAdaptiveFilterState=Passed"
            },
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = cycleId,
                Symbol = "BTCUSDT",
                UniverseSource = "config",
                ObservedAtUtc = now,
                IsEligible = true,
                Score = 70m,
                Rank = 2,
                IsTopCandidate = true,
                ScoringSummary = "ScannerRankingMode=ClassicalFallback; ScannerClassicalScore=95; ScannerCombinedScore=95; ScannerRankingFallbackReason=OutcomeCoverageBelowThreshold; ScannerAdaptiveFilterState=Passed"
            },
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = cycleId,
                Symbol = "SOLUSDT",
                UniverseSource = "config",
                ObservedAtUtc = now,
                IsEligible = false,
                RejectionReason = "AdaptiveFilterLowQualitySetup",
                Score = 0m,
                ScoringSummary = "ScannerRankingMode=AdvisoryCombined; ScannerClassicalScore=40; ScannerCombinedScore=25; ScannerAdaptiveFilterState=Suppressed; ScannerAdaptiveFilterReason=LowAdvisoryScoreAndWeakClassicalScore"
            });
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(1, snapshot.MarketScanner.AiRankingFallbackCount);
        Assert.Equal(1, snapshot.MarketScanner.AiRankingSuppressionCount);
        Assert.Equal(1, snapshot.MarketScanner.AiRankingTopCandidateChangedCount);
    }

    [Fact]
    public async Task GetSnapshotAsync_ProvidesAiRankingNotActiveStatus_WhenLatestCycleHasNoEligibleRankedCandidates()
    {
        var now = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);
        var cycleId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = cycleId,
            StartedAtUtc = now.AddSeconds(-2),
            CompletedAtUtc = now,
            UniverseSource = "config+registry",
            ScannedSymbolCount = 2,
            EligibleCandidateCount = 0,
            TopCandidateCount = 0,
            Summary = "scan complete"
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
                RejectionReason = "NoEnabledBotForSymbol",
                Score = 0m,
                ScoringSummary = "ScannerRankingMode=NotRanked; ScannerAdaptiveFilterState=NotApplicable; ScannerAdaptiveFilterReason=CandidateIneligible"
            },
            new MarketScannerCandidate
            {
                Id = Guid.NewGuid(),
                ScanCycleId = cycleId,
                Symbol = "SOLUSDT",
                UniverseSource = "config",
                ObservedAtUtc = now,
                IsEligible = false,
                RejectionReason = "StrategyNoSignalCandidate",
                Score = 0m,
                ScoringSummary = "ScannerRankingMode=NotRanked; ScannerAdaptiveFilterState=NotApplicable; ScannerAdaptiveFilterReason=CandidateIneligible"
            });
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal("AI ranking not active", snapshot.MarketScanner.AiRankingStatusTitle);
        Assert.Equal("AI ranking not active: no eligible ranked candidates in latest scanner cycle.", snapshot.MarketScanner.AiRankingStatusSummary);
        Assert.Contains("NotRanked", snapshot.MarketScanner.AiRankingStatusReason, StringComparison.Ordinal);
        Assert.Contains("CandidateIneligible", snapshot.MarketScanner.AiRankingStatusReason, StringComparison.Ordinal);
        Assert.Contains("NoEnabledBotForSymbol", snapshot.MarketScanner.AiRankingStatusReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshotAsync_ProvidesAiRankingDisabledRollbackState_WhenLatestEligibleCandidatesAreClassical()
    {
        var now = new DateTime(2026, 4, 25, 10, 0, 0, DateTimeKind.Utc);
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
            UniverseSource = "config",
            ObservedAtUtc = now,
            IsEligible = true,
            Score = 95m,
            Rank = 1,
            IsTopCandidate = true,
            ScoringSummary = "ScannerRankingMode=Disabled; ScannerClassicalScore=95; ScannerCombinedScore=95; ScannerAdaptiveFilterState=Disabled"
        });
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal("AI ranking disabled", snapshot.MarketScanner.AiRankingStatusTitle);
        Assert.Equal("AI ranking disabled — classical ranking active.", snapshot.MarketScanner.AiRankingStatusSummary);
        Assert.Equal("Disabled", snapshot.MarketScanner.AiRankingStatusReason);
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
            sharedMarketDataCacheObservabilityCollector: cacheCollector);

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
    public async Task Heartbeat_IncludesLastDiskPressureCheck()
    {
        var now = new DateTime(2026, 4, 25, 9, 30, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();
        var ultraDebugLogService = new FakeUltraDebugLogService
        {
            HealthSnapshot = new UltraDebugLogHealthSnapshot(
                DiskPressureState: "Warning",
                FreeBytes: 768L * 1024L * 1024L,
                FreePercent: 12.5m,
                ThresholdBytes: 512L * 1024L * 1024L,
                AffectedLogBuckets: ["ultra_debug"],
                LastCheckedAtUtc: now,
                LastEscalationReason: "disk_pressure",
                IsWritable: true,
                IsTailAvailable: true,
                IsExportAvailable: true,
                LastRetentionCompletedAtUtc: now.AddMinutes(-5),
                LastRetentionReasonCode: "Completed",
                LastRetentionSucceeded: true,
                IsNormalFallbackMode: true,
                AutoDisabledReason: "disk_pressure",
                IsEnabled: false)
        };

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()),
            ultraDebugLogService: ultraDebugLogService);

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal("Warning", snapshot.UltraDebugLogHealth.DiskPressureState);
        Assert.Equal(now, snapshot.UltraDebugLogHealth.LastCheckedAtUtc);
        Assert.Equal("disk_pressure", snapshot.UltraDebugLogHealth.LastEscalationReason);
        Assert.Equal("Completed", snapshot.UltraDebugLogHealth.LastRetentionReasonCode);
        Assert.True(snapshot.UltraDebugLogHealth.LastRetentionSucceeded);
    }

    [Fact]
    public async Task AdminDashboardReadModel_ReturnsBoundedOperationalSummary()
    {
        var now = DateTime.UtcNow;
        await using var dbContext = CreateDbContext();

        var decisions = Enumerable.Range(0, 220)
            .Select(index => CreateShadowDecision(
                ownerUserId: $"user-{index % 3}",
                botId: Guid.NewGuid(),
                correlationId: $"corr-{index}",
                symbol: index % 2 == 0 ? "BTCUSDT" : "ETHUSDT",
                timeframe: "1m",
                evaluatedAtUtc: now.AddMinutes(-index),
                noSubmitReason: "EntryDirectionModeBlocked"))
            .ToArray();
        dbContext.AiShadowDecisions.AddRange(decisions);
        dbContext.MarketScannerHandoffAttempts.AddRange(
            Enumerable.Range(0, 60).Select(index => new MarketScannerHandoffAttempt
            {
                Id = Guid.NewGuid(),
                ScanCycleId = Guid.NewGuid(),
                SelectedSymbol = "SOLUSDT",
                SelectedTimeframe = "1m",
                SelectedAtUtc = now.AddMinutes(-index),
                SelectionReason = "Top-ranked eligible candidate selected.",
                StrategyDecisionOutcome = "Persisted",
                ExecutionRequestStatus = "Blocked",
                BlockerCode = "EntryDirectionModeBlocked",
                CorrelationId = $"handoff-{index}",
                CompletedAtUtc = now.AddMinutes(-index)
            }));
        await dbContext.SaveChangesAsync();

        for (var index = 0; index < decisions.Length; index++)
        {
            decisions[index].CreatedDate = now.AddMinutes(-index);
            decisions[index].UpdatedDate = decisions[index].CreatedDate;
        }

        dbContext.AiShadowDecisionOutcomes.AddRange(
            decisions.Take(200).Select(decision => CreateShadowOutcome(decision, now.AddMinutes(1))));
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();
        var operational = snapshot.OperationalObservability;

        Assert.Equal(200, operational.RecentAiShadowDecisionCount);
        Assert.Equal(200, operational.RecentAiShadowDecisionOutcomeCount);
        Assert.Equal(100m, operational.AiShadowOutcomeCoveragePercent);
        var blockedReason = Assert.Single(operational.BlockedReasons);
        Assert.Equal("EntryDirectionModeBlocked", blockedReason.ReasonCode);
        Assert.Equal(50, blockedReason.Count);
    }

    [Fact]
    public async Task AdminDashboardReadModel_IncludesScannerAndHandoffSummary()
    {
        var now = DateTime.UtcNow;
        var cycleId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();

        dbContext.MarketScannerCycles.Add(new MarketScannerCycle
        {
            Id = cycleId,
            StartedAtUtc = now.AddSeconds(-10),
            CompletedAtUtc = now,
            UniverseSource = "config",
            ScannedSymbolCount = 3,
            EligibleCandidateCount = 1,
            TopCandidateCount = 1,
            BestCandidateSymbol = "BTCUSDT",
            BestCandidateScore = 87m,
            Summary = "Bounded scanner summary"
        });
        dbContext.MarketScannerCandidates.Add(new MarketScannerCandidate
        {
            Id = Guid.NewGuid(),
            ScanCycleId = cycleId,
            Symbol = "BTCUSDT",
            UniverseSource = "config",
            ObservedAtUtc = now,
            IsEligible = true,
            Score = 87m,
            Rank = 1,
            IsTopCandidate = true
        });
        dbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = cycleId,
            SelectedCandidateId = Guid.NewGuid(),
            SelectedSymbol = "BTCUSDT",
            SelectedTimeframe = "1m",
            SelectedAtUtc = now,
            SelectionReason = "Top-ranked eligible candidate selected.",
            StrategyDecisionOutcome = "Persisted",
            ExecutionRequestStatus = "Blocked",
            BlockerCode = "StrategyVetoed",
            BlockerSummary = "StrategyVetoed: Exposure limit breached.",
            CorrelationId = "corr-handoff",
            CompletedAtUtc = now
        });
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();
        var operational = snapshot.OperationalObservability;

        Assert.Equal(now, operational.LastScannerCycleCompletedAtUtc);
        Assert.Equal("BTCUSDT", operational.TopCandidateSymbol);
        Assert.Equal(1, operational.EligibleCandidateCount);
        Assert.Contains("Bounded scanner summary", operational.LastScannerCycleSummary, StringComparison.Ordinal);
        Assert.Contains("BTCUSDT", operational.LatestHandoffSummary ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("StrategyVetoed", operational.LatestHandoffSummary ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("Watching", operational.ExecutionReadiness.State);
        Assert.Contains(operational.BlockedReasons, item => item.ReasonCode == "StrategyVetoed");
    }

    [Fact]
    public async Task AdminDashboardReadModel_IncludesAiShadowOutcomeCoverage()
    {
        var now = DateTime.UtcNow;
        await using var dbContext = CreateDbContext();
        var decisions = Enumerable.Range(0, 4)
            .Select(index => CreateShadowDecision(
                ownerUserId: "user-coverage",
                botId: Guid.NewGuid(),
                correlationId: $"coverage-{index}",
                symbol: "SOLUSDT",
                timeframe: "1m",
                evaluatedAtUtc: now.AddMinutes(-index),
                noSubmitReason: "EntryDirectionModeBlocked"))
            .ToArray();
        dbContext.AiShadowDecisions.AddRange(decisions);
        await dbContext.SaveChangesAsync();

        for (var index = 0; index < decisions.Length; index++)
        {
            decisions[index].CreatedDate = now.AddMinutes(-index);
            decisions[index].UpdatedDate = decisions[index].CreatedDate;
        }

        dbContext.AiShadowDecisionOutcomes.AddRange(
            decisions.Take(2).Select(decision => CreateShadowOutcome(decision, now.AddMinutes(2))));
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(4, snapshot.OperationalObservability.RecentAiShadowDecisionCount);
        Assert.Equal(2, snapshot.OperationalObservability.RecentAiShadowDecisionOutcomeCount);
        Assert.Equal(50m, snapshot.OperationalObservability.AiShadowOutcomeCoveragePercent);
        Assert.Contains("2/4 recent shadow decisions scored", snapshot.OperationalObservability.AiShadowOutcomeCoverageSummary, StringComparison.Ordinal);
        Assert.Contains(snapshot.OperationalObservability.NoSubmitReasons, item => item.ReasonCode == "EntryDirectionModeBlocked");
    }

    [Fact]
    public async Task AdminDashboardReadModel_IncludesLogAndDiskHealth()
    {
        var now = DateTime.UtcNow;
        await using var dbContext = CreateDbContext();
        var ultraDebugLogService = new FakeUltraDebugLogService
        {
            HealthSnapshot = new UltraDebugLogHealthSnapshot(
                DiskPressureState: "Warning",
                FreeBytes: 768L * 1024L * 1024L,
                FreePercent: 12.5m,
                ThresholdBytes: 512L * 1024L * 1024L,
                AffectedLogBuckets: ["normal", "ultra_debug"],
                LastCheckedAtUtc: now,
                LastEscalationReason: "disk_pressure",
                IsWritable: true,
                IsTailAvailable: true,
                IsExportAvailable: true,
                LastRetentionCompletedAtUtc: now.AddMinutes(-5),
                LastRetentionReasonCode: "Completed",
                LastRetentionSucceeded: true,
                IsNormalFallbackMode: true,
                AutoDisabledReason: "disk_pressure",
                IsEnabled: false)
        };

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()),
            ultraDebugLogService: ultraDebugLogService);

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal("Warning", snapshot.OperationalObservability.LogSystemState);
        Assert.Equal("Warning", snapshot.OperationalObservability.DiskPressureState);
        Assert.Contains("Last janitor", snapshot.OperationalObservability.JanitorSummary, StringComparison.Ordinal);
        Assert.Contains("Masked log export available.", snapshot.OperationalObservability.ExportSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminDashboardReadModel_ParsesExitPnlEvidence_FromDecisionSummaries()
    {
        var now = new DateTime(2026, 4, 27, 12, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();

        dbContext.DecisionTraces.AddRange(
            CreateDecisionTrace(
                symbol: "SOLUSDT",
                decisionReasonCode: "ExitCloseOnlyAllowedTakeProfit",
                decisionSummary: "ExitPnlGuard=Allowed; ExitReason=ReverseSignal; ReasonCode=ExitCloseOnlyAllowedTakeProfit; PositionDirection=Short; EntryPrice=85.35; ExitPrice=84.9; CloseSide=Buy; ReduceOnly=True; EstimatedPnlQuote=0.027; EstimatedPnlPct=0.53; MinimumProfitPct=0;",
                createdAtUtc: now.AddMinutes(-3)),
            CreateDecisionTrace(
                symbol: "SOLUSDT",
                decisionReasonCode: "ExitCloseOnlyBlockedUnprofitableShort",
                decisionSummary: "ExitPnlGuard=Blocked; ExitReason=BlockedUnprofitable; ReasonCode=ExitCloseOnlyBlockedUnprofitableShort; PositionDirection=Short; EntryPrice=85.35; ExitPrice=85.5; CloseSide=Buy; ReduceOnly=True; EstimatedPnlQuote=-0.009; EstimatedPnlPct=-0.17; MinimumProfitPct=0;",
                createdAtUtc: now.AddMinutes(-2)),
            CreateDecisionTrace(
                symbol: "SOLUSDT",
                decisionReasonCode: "StopLossTriggered",
                decisionSummary: "Runtime exit quality triggered StopLoss for SOLUSDT. ExitReason=StopLoss; ReasonCode=StopLossTriggered; PositionDirection=Long; EntryPrice=85.1; ExitPrice=84.7; CloseSide=Sell; ReduceOnly=True; EstimatedPnlQuote=-0.024; EstimatedPnlPct=-0.47;",
                createdAtUtc: now.AddMinutes(-1)),
            CreateDecisionTrace(
                symbol: "BTCUSDT",
                decisionReasonCode: "TakeProfitTriggered",
                decisionSummary: "Runtime exit quality triggered TakeProfit for BTCUSDT. ExitReason=TakeProfit; ReasonCode=TakeProfitTriggered; PositionDirection=Long; EntryPrice=64000; ExitPrice=64200; CloseSide=Sell; ReduceOnly=True; EstimatedPnlQuote=12.5; EstimatedPnlPct=0.31;",
                createdAtUtc: now));
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();
        var exitEvidence = snapshot.OperationalObservability.ExitPnlEvidence;

        Assert.Equal(4, exitEvidence.LastExitCount);
        Assert.Equal(2, exitEvidence.ProfitableExitCount);
        Assert.Equal(1, exitEvidence.UnprofitableExitBlockedCount);
        Assert.Equal(1, exitEvidence.StopLossExitCount);
        Assert.Equal(1, exitEvidence.TakeProfitExitCount);
        Assert.Equal("TakeProfit", exitEvidence.LastExitReason);
        Assert.Equal(12.5m, exitEvidence.LastEstimatedPnlQuote);
        Assert.Equal(0.31m, exitEvidence.LastEstimatedPnlPct);
        Assert.Equal(now, exitEvidence.LastExitAtUtc);
        Assert.Equal("BTCUSDT", exitEvidence.LastExitSymbol);
        Assert.Equal("Sell", exitEvidence.LastExitSide);
        Assert.True(exitEvidence.LastExitReduceOnly);
    }

    [Fact]
    public async Task AdminDashboardReadModel_PairsExecutionOrdersIntoStrategyProfitQualityEvidence()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();

        var strategyId = Guid.NewGuid();
        var strategyVersionId = Guid.NewGuid();
        var reverseEntrySignalId = Guid.NewGuid();
        var stopLossEntrySignalId = Guid.NewGuid();
        var stopLossExitSignalId = Guid.NewGuid();
        var manualEntrySignalId = Guid.NewGuid();

        dbContext.TradingStrategySignals.AddRange(
            CreateStrategySignal(reverseEntrySignalId, strategyId, strategyVersionId, StrategySignalType.Entry, "SOLUSDT", now.AddMinutes(-30)),
            CreateStrategySignal(stopLossEntrySignalId, strategyId, strategyVersionId, StrategySignalType.Entry, "SOLUSDT", now.AddMinutes(-20)),
            CreateStrategySignal(stopLossExitSignalId, strategyId, strategyVersionId, StrategySignalType.Exit, "SOLUSDT", now.AddMinutes(-19)),
            CreateStrategySignal(manualEntrySignalId, strategyId, strategyVersionId, StrategySignalType.Entry, "BTCUSDT", now.AddMinutes(-10)));

        dbContext.DecisionTraces.Add(CreateDecisionTrace(
            symbol: "SOLUSDT",
            decisionReasonCode: "StopLossTriggered",
            decisionSummary: "Runtime exit quality triggered StopLoss for SOLUSDT. ExitReason=StopLoss; EntrySource=n/a; ExitSource=StopLoss; ReasonCode=StopLossTriggered; PositionDirection=Long; EntryPrice=100; ExitPrice=95; CloseSide=Sell; ReduceOnly=True; EstimatedPnlQuote=-5; EstimatedPnlPct=-5; PeakReferencePrice=102;",
            createdAtUtc: now.AddMinutes(-19),
            strategySignalId: stopLossExitSignalId));

        dbContext.ExecutionOrders.AddRange(
            CreateExecutionOrder(
                strategySignalId: reverseEntrySignalId,
                strategyId: strategyId,
                strategyVersionId: strategyVersionId,
                strategyKey: "alpha-core",
                symbol: "SOLUSDT",
                signalType: StrategySignalType.Entry,
                side: ExecutionOrderSide.Buy,
                quantity: 1m,
                price: 100m,
                reduceOnly: false,
                createdAtUtc: now.AddMinutes(-30)),
            CreateExecutionOrder(
                strategySignalId: reverseEntrySignalId,
                strategyId: strategyId,
                strategyVersionId: strategyVersionId,
                strategyKey: "alpha-core",
                symbol: "SOLUSDT",
                signalType: StrategySignalType.Exit,
                side: ExecutionOrderSide.Sell,
                quantity: 1m,
                price: 105m,
                reduceOnly: true,
                createdAtUtc: now.AddMinutes(-29)),
            CreateExecutionOrder(
                strategySignalId: stopLossEntrySignalId,
                strategyId: strategyId,
                strategyVersionId: strategyVersionId,
                strategyKey: "alpha-core",
                symbol: "SOLUSDT",
                signalType: StrategySignalType.Entry,
                side: ExecutionOrderSide.Buy,
                quantity: 1m,
                price: 100m,
                reduceOnly: false,
                createdAtUtc: now.AddMinutes(-20)),
            CreateExecutionOrder(
                strategySignalId: stopLossExitSignalId,
                strategyId: strategyId,
                strategyVersionId: strategyVersionId,
                strategyKey: "alpha-core",
                symbol: "SOLUSDT",
                signalType: StrategySignalType.Exit,
                side: ExecutionOrderSide.Sell,
                quantity: 1m,
                price: 95m,
                reduceOnly: true,
                createdAtUtc: now.AddMinutes(-19)),
            CreateExecutionOrder(
                strategySignalId: manualEntrySignalId,
                strategyId: strategyId,
                strategyVersionId: strategyVersionId,
                strategyKey: "beta-core",
                symbol: "BTCUSDT",
                signalType: StrategySignalType.Entry,
                side: ExecutionOrderSide.Sell,
                quantity: 2m,
                price: 50m,
                reduceOnly: false,
                createdAtUtc: now.AddMinutes(-10)),
            CreateExecutionOrder(
                strategySignalId: Guid.NewGuid(),
                strategyId: strategyId,
                strategyVersionId: strategyVersionId,
                strategyKey: "__admin_manual_close__",
                symbol: "BTCUSDT",
                signalType: StrategySignalType.Exit,
                side: ExecutionOrderSide.Buy,
                quantity: 2m,
                price: 47.5m,
                reduceOnly: true,
                createdAtUtc: now.AddMinutes(-9)));
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();
        var profitQuality = snapshot.OperationalObservability.StrategyProfitQuality;

        Assert.Equal(3, profitQuality.PairedTradeCount);
        Assert.Equal(0, profitQuality.UnpairedEntryCount);
        Assert.Equal(0, profitQuality.UnpairedExitCount);
        Assert.Equal(2, profitQuality.WinCount);
        Assert.Equal(1, profitQuality.LossCount);
        Assert.Equal(66.67m, profitQuality.WinRatePercent);
        Assert.Equal(5m, profitQuality.AverageProfitQuote);
        Assert.Equal(-5m, profitQuality.AverageLossQuote);
        Assert.Equal(1.66666667m, profitQuality.AverageNetPnlQuote);
        Assert.Equal(1.66666667m, profitQuality.AverageNetPnlPct);
        Assert.Equal(2m, profitQuality.MaxFavorableExcursionQuote);
        Assert.Null(profitQuality.MaxAdverseExcursionQuote);

        var reverseRow = Assert.Single(
            profitQuality.StrategySummaries,
            row =>
                row.StrategyKey == "alpha-core" &&
                row.Symbol == "SOLUSDT" &&
                row.ExitSource == "ReverseSignal");
        Assert.Equal(1, reverseRow.WinCount);
        Assert.Equal(5m, reverseRow.AverageNetPnlQuote);

        var stopLossRow = Assert.Single(
            profitQuality.StrategySummaries,
            row =>
                row.StrategyKey == "alpha-core" &&
                row.Symbol == "SOLUSDT" &&
                row.ExitSource == "StopLoss");
        Assert.Equal(1, stopLossRow.LossCount);
        Assert.Equal(-5m, stopLossRow.AverageNetPnlQuote);
        Assert.Equal(2m, stopLossRow.MaxFavorableExcursionQuote);

        var manualRow = Assert.Single(
            profitQuality.StrategySummaries,
            row =>
                row.StrategyKey == "beta-core" &&
                row.Symbol == "BTCUSDT" &&
                row.ExitSource == "Manual");
        Assert.Equal(1, manualRow.WinCount);
        Assert.Equal(5m, manualRow.AverageNetPnlQuote);
    }

    [Fact]
    public async Task AdminDashboardReadModel_HandlesUnpairedProfitQualityRowsSafely_WhenPriceOrPairingDataIsMissing()
    {
        var now = new DateTime(2026, 4, 29, 13, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();

        var strategyId = Guid.NewGuid();
        var strategyVersionId = Guid.NewGuid();
        var entrySignalId = Guid.NewGuid();

        dbContext.TradingStrategySignals.Add(CreateStrategySignal(
            entrySignalId,
            strategyId,
            strategyVersionId,
            StrategySignalType.Entry,
            "SOLUSDT",
            now.AddMinutes(-5)));

        dbContext.ExecutionOrders.AddRange(
            CreateExecutionOrder(
                strategySignalId: entrySignalId,
                strategyId: strategyId,
                strategyVersionId: strategyVersionId,
                strategyKey: "alpha-core",
                symbol: "SOLUSDT",
                signalType: StrategySignalType.Entry,
                side: ExecutionOrderSide.Buy,
                quantity: 1m,
                price: 100m,
                reduceOnly: false,
                createdAtUtc: now.AddMinutes(-5)),
            CreateExecutionOrder(
                strategySignalId: Guid.NewGuid(),
                strategyId: strategyId,
                strategyVersionId: strategyVersionId,
                strategyKey: "alpha-core",
                symbol: "SOLUSDT",
                signalType: StrategySignalType.Exit,
                side: ExecutionOrderSide.Sell,
                quantity: 1m,
                price: 0m,
                reduceOnly: true,
                createdAtUtc: now.AddMinutes(-4)),
            CreateExecutionOrder(
                strategySignalId: Guid.NewGuid(),
                strategyId: strategyId,
                strategyVersionId: strategyVersionId,
                strategyKey: "__admin_manual_close__",
                symbol: "BTCUSDT",
                signalType: StrategySignalType.Exit,
                side: ExecutionOrderSide.Buy,
                quantity: 1m,
                price: 48m,
                reduceOnly: true,
                createdAtUtc: now.AddMinutes(-3)));
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();
        var profitQuality = snapshot.OperationalObservability.StrategyProfitQuality;

        Assert.Equal(0, profitQuality.PairedTradeCount);
        Assert.Equal(1, profitQuality.UnpairedEntryCount);
        Assert.Equal(2, profitQuality.UnpairedExitCount);
        Assert.Empty(profitQuality.StrategySummaries);
        Assert.Contains("No paired trades found", profitQuality.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminDashboardReadModel_ProjectsPilotConfigAndPrivateSyncEvidence()
    {
        var now = new DateTime(2026, 4, 29, 9, 15, 0, DateTimeKind.Utc);
        var botId = Guid.NewGuid();
        var activeAccountId = Guid.Parse("8F61C0E3-D082-4F28-4080-08DE97E95FBB");
        await using var dbContext = CreateDbContext();

        dbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "pilot-owner",
            ExchangeAccountId = activeAccountId,
            Name = "scope-test-sol-03",
            StrategyKey = "pilot-core",
            Symbol = "SOLUSDT",
            IsEnabled = true,
            CreatedDate = now.AddDays(-1),
            UpdatedDate = now.AddHours(-2)
        });
        dbContext.ExchangePositions.Add(new ExchangePosition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "pilot-owner",
            ExchangeAccountId = activeAccountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "SOLUSDT",
            PositionSide = "Long",
            Quantity = 0.14m,
            EntryPrice = 83.89m,
            BreakEvenPrice = 83.923556m,
            UnrealizedProfit = 0.12m,
            MarginType = "isolated",
            ExchangeUpdatedAtUtc = now.AddSeconds(-20),
            SyncedAtUtc = now.AddSeconds(-15),
            CreatedDate = now.AddMinutes(-5),
            UpdatedDate = now.AddSeconds(-10)
        });
        dbContext.ExchangeAccountSyncStates.AddRange(
            new ExchangeAccountSyncState
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "pilot-owner",
                ExchangeAccountId = activeAccountId,
                Plane = ExchangeDataPlane.Futures,
                PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
                DriftStatus = ExchangeStateDriftStatus.InSync,
                DriftSummary = "BalanceMismatches=0; PositionMismatches=0; SnapshotSource=Binance.PrivateRest.Account+PositionRisk",
                LastDriftDetectedAtUtc = now.AddMinutes(-1),
                LastBalanceSyncedAtUtc = now.AddSeconds(-25),
                LastPositionSyncedAtUtc = now.AddSeconds(-20),
                LastStateReconciledAtUtc = now.AddSeconds(-10),
                ConsecutiveStreamFailureCount = 0,
                LastErrorCode = null,
                CreatedDate = now.AddMinutes(-2),
                UpdatedDate = now.AddSeconds(-5)
            },
            new ExchangeAccountSyncState
            {
                Id = Guid.NewGuid(),
                OwnerUserId = "inactive-owner",
                ExchangeAccountId = Guid.NewGuid(),
                Plane = ExchangeDataPlane.Futures,
                PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Disconnected,
                DriftStatus = ExchangeStateDriftStatus.Unknown,
                DriftSummary = "malformed",
                LastErrorCode = "CredentialAccessBlocked",
                CreatedDate = now.AddMinutes(-3),
                UpdatedDate = now.AddMinutes(-1)
            });
        dbContext.ExecutionOrders.Add(new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "pilot-owner",
            TradingStrategyId = Guid.NewGuid(),
            TradingStrategyVersionId = Guid.NewGuid(),
            StrategySignalId = Guid.NewGuid(),
            SignalType = StrategySignalType.Exit,
            BotId = botId,
            ExchangeAccountId = activeAccountId,
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = "pilot-core",
            Symbol = "SOLUSDT",
            Timeframe = "1m",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            Side = ExecutionOrderSide.Sell,
            OrderType = ExecutionOrderType.Market,
            Quantity = 0.14m,
            Price = 83.80m,
            ReduceOnly = true,
            ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet,
            ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet,
            State = ExecutionOrderState.Rejected,
            IdempotencyKey = "pilot-private-plane-stale",
            RootCorrelationId = "corr-private-plane-stale",
            FailureCode = "PrivatePlaneStale",
            FailureDetail = "Private plane stale.",
            RejectionStage = ExecutionRejectionStage.PreSubmit,
            SubmittedToBroker = false,
            LastStateChangedAtUtc = now.AddMinutes(-5),
            CreatedDate = now.AddMinutes(-5),
            UpdatedDate = now.AddMinutes(-5)
        });
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()),
            Options.Create(new BotExecutionPilotOptions
            {
                AutoManageAdoptedPositions = true,
                ExecutionDispatchMode = ExecutionEnvironment.BinanceTestnet,
                AllowedSymbols = ["SOLUSDT"],
                AllowedBotIds = [botId.ToString("D")],
                AllowedUserIds = ["pilot-owner"]
            }));

        var snapshot = await service.GetSnapshotAsync();
        var pilotConfig = snapshot.OperationalObservability.PilotConfigEvidence;
        var privateSync = snapshot.OperationalObservability.PrivateSyncEvidence;

        Assert.True(pilotConfig.AutoManageAdoptedPositions);
        Assert.Equal("BinanceTestnet", pilotConfig.ExecutionDispatchMode);
        Assert.Equal(1, pilotConfig.AllowedSymbolCount);
        Assert.Equal("SOLUSDT", pilotConfig.AllowedSymbolsSummary);
        Assert.Equal(1, pilotConfig.AllowedBotIdCount);
        Assert.Equal(1, pilotConfig.AllowedUserIdCount);
        Assert.Null(pilotConfig.AllowedExchangeAccountIdCount);

        Assert.Equal("Healthy", privateSync.State);
        Assert.Equal(activeAccountId.ToString("D"), privateSync.ExchangeAccountId);
        Assert.Equal("Futures", privateSync.Plane);
        Assert.Equal("Connected", privateSync.PrivateStreamConnectionState);
        Assert.Equal("InSync", privateSync.DriftStatus);
        Assert.Equal(0, privateSync.BalanceMismatches);
        Assert.Equal(0, privateSync.PositionMismatches);
        Assert.Equal("Binance.PrivateRest.Account+PositionRisk", privateSync.SnapshotSource);
        Assert.Equal(10, privateSync.SyncAgeSeconds);
        Assert.Equal(1, privateSync.InactiveBlockedCredentialAccountCount);
        Assert.Equal(1, privateSync.PrivatePlaneStaleRejectCount);
    }

    [Fact]
    public async Task AdminDashboardReadModel_DriftSummaryParser_IsSafeForMalformedValues()
    {
        var now = new DateTime(2026, 4, 29, 9, 30, 0, DateTimeKind.Utc);
        var botId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await using var dbContext = CreateDbContext();

        dbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "pilot-owner",
            ExchangeAccountId = accountId,
            Name = "pilot-bot",
            StrategyKey = "pilot-core",
            Symbol = "SOLUSDT",
            IsEnabled = true,
            CreatedDate = now.AddDays(-1),
            UpdatedDate = now.AddMinutes(-20)
        });
        dbContext.ExchangePositions.Add(new ExchangePosition
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "pilot-owner",
            ExchangeAccountId = accountId,
            Plane = ExchangeDataPlane.Futures,
            Symbol = "SOLUSDT",
            PositionSide = "Short",
            Quantity = -0.14m,
            EntryPrice = 83.89m,
            BreakEvenPrice = 83.923556m,
            UnrealizedProfit = -0.12m,
            MarginType = "isolated",
            ExchangeUpdatedAtUtc = now.AddSeconds(-20),
            SyncedAtUtc = now.AddSeconds(-15),
            CreatedDate = now.AddMinutes(-5),
            UpdatedDate = now.AddSeconds(-10)
        });
        dbContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "pilot-owner",
            ExchangeAccountId = accountId,
            Plane = ExchangeDataPlane.Futures,
            PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
            DriftStatus = ExchangeStateDriftStatus.InSync,
            DriftSummary = "not-a-kv-summary",
            CreatedDate = now.AddMinutes(-1),
            UpdatedDate = now.AddSeconds(-5)
        });
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()),
            Options.Create(new BotExecutionPilotOptions
            {
                AllowedSymbols = ["SOLUSDT"]
            }));

        var snapshot = await service.GetSnapshotAsync();
        var privateSync = snapshot.OperationalObservability.PrivateSyncEvidence;

        Assert.Equal("n/a", privateSync.SnapshotSource);
        Assert.Null(privateSync.BalanceMismatches);
        Assert.Null(privateSync.PositionMismatches);
        Assert.Equal("not-a-kv-summary", privateSync.DriftSummary);
    }

    [Fact]
    public async Task AdminDashboardReadModel_UsesEmptyExitPnlEvidence_WhenNoExitTokensExist()
    {
        var now = new DateTime(2026, 4, 27, 12, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();
        dbContext.DecisionTraces.Add(CreateDecisionTrace(
            symbol: "SOLUSDT",
            decisionReasonCode: "NoSignalCandidate",
            decisionSummary: "Strategy did not produce an executable candidate.",
            createdAtUtc: now));
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();
        var exitEvidence = snapshot.OperationalObservability.ExitPnlEvidence;

        Assert.Equal(0, exitEvidence.LastExitCount);
        Assert.Null(exitEvidence.LastExitReason);
        Assert.Null(exitEvidence.LastEstimatedPnlQuote);
        Assert.Null(exitEvidence.LastExitAtUtc);
        Assert.Null(exitEvidence.LastExitReduceOnly);
    }

    [Fact]
    public async Task AdminDashboardReadModel_DoesNotExposeSensitiveData()
    {
        var now = DateTime.UtcNow;
        await using var dbContext = CreateDbContext();
        var decision = CreateShadowDecision(
            ownerUserId: "owner-secret-user",
            botId: Guid.NewGuid(),
            correlationId: "corr-secret-123",
            symbol: "BTCUSDT",
            timeframe: "1m",
            evaluatedAtUtc: now,
            noSubmitReason: "EntryDirectionModeBlocked");
        dbContext.AiShadowDecisions.Add(decision);
        dbContext.MarketScannerHandoffAttempts.Add(new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = Guid.NewGuid(),
            SelectedSymbol = "BTCUSDT",
            SelectedTimeframe = "1m",
            SelectedAtUtc = now,
            SelectionReason = "Top-ranked eligible candidate selected.",
            StrategyDecisionOutcome = "Persisted",
            ExecutionRequestStatus = "Blocked",
            BlockerCode = "EntryDirectionModeBlocked",
            CorrelationId = "corr-secret-123",
            CompletedAtUtc = now
        });
        await dbContext.SaveChangesAsync();

        decision.CreatedDate = now;
        decision.UpdatedDate = now;
        await dbContext.SaveChangesAsync();

        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();
        var rendered = string.Join(
            " | ",
            snapshot.OperationalObservability.SystemHealthSummary,
            snapshot.OperationalObservability.WorkerHeartbeatSummary,
            snapshot.OperationalObservability.LastScannerCycleSummary,
            snapshot.OperationalObservability.LatestHandoffSummary,
            snapshot.OperationalObservability.ExecutionReadiness.Summary,
            snapshot.OperationalObservability.AiShadowOutcomeCoverageSummary,
            snapshot.OperationalObservability.JanitorSummary,
            snapshot.OperationalObservability.ExportSummary,
            snapshot.OperationalObservability.PilotConfigEvidence.AllowedSymbolsSummary,
            snapshot.OperationalObservability.PrivateSyncEvidence.Summary,
            snapshot.OperationalObservability.PrivateSyncEvidence.DriftSummary,
            string.Join(" | ", snapshot.OperationalObservability.NoSubmitReasons.Select(item => item.ReasonCode)),
            string.Join(" | ", snapshot.OperationalObservability.BlockedReasons.Select(item => item.ReasonCode)),
            string.Join(" | ", snapshot.OperationalObservability.CriticalWarnings.Select(item => item.Summary)));

        Assert.DoesNotContain("owner-secret-user", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("corr-secret-123", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminDashboardReadModel_HandlesEmptyDatabase()
    {
        await using var dbContext = CreateDbContext();
        var now = DateTime.UtcNow;
        var service = new AdminMonitoringReadModelService(
            dbContext,
            new MemoryCache(new MemoryCacheOptions()),
            new FixedTimeProvider(now),
            Options.Create(new DataLatencyGuardOptions()));

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal("Unknown", snapshot.OperationalObservability.OverallState);
        Assert.Contains("No health", snapshot.OperationalObservability.SystemHealthSummary, StringComparison.Ordinal);
        Assert.Contains("No worker heartbeat", snapshot.OperationalObservability.WorkerHeartbeatSummary, StringComparison.Ordinal);
        Assert.Equal("No recent shadow decisions.", snapshot.OperationalObservability.AiShadowOutcomeCoverageSummary);
        Assert.Equal("No janitor heartbeat yet.", snapshot.OperationalObservability.JanitorSummary);
        Assert.Empty(snapshot.OperationalObservability.BlockedReasons);
        Assert.Empty(snapshot.OperationalObservability.NoSubmitReasons);
        Assert.Equal(0, snapshot.OperationalObservability.ExitPnlEvidence.LastExitCount);
        Assert.Null(snapshot.OperationalObservability.ExitPnlEvidence.LastExitReason);
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

    private static AiShadowDecision CreateShadowDecision(
        string ownerUserId,
        Guid botId,
        string correlationId,
        string symbol,
        string timeframe,
        DateTime evaluatedAtUtc,
        string noSubmitReason)
    {
        return new AiShadowDecision
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            BotId = botId,
            CorrelationId = correlationId,
            StrategyKey = "strategy-test",
            Symbol = symbol,
            Timeframe = timeframe,
            EvaluatedAtUtc = evaluatedAtUtc,
            AiReasonSummary = "shadow-reason",
            AiProviderName = "ShadowLinear",
            FinalAction = "NoSubmit",
            HypotheticalSubmitAllowed = false,
            NoSubmitReason = noSubmitReason
        };
    }

    private static AiShadowDecisionOutcome CreateShadowOutcome(AiShadowDecision decision, DateTime scoredAtUtc)
    {
        return new AiShadowDecisionOutcome
        {
            Id = Guid.NewGuid(),
            OwnerUserId = decision.OwnerUserId,
            AiShadowDecisionId = decision.Id,
            BotId = decision.BotId,
            Symbol = decision.Symbol,
            Timeframe = decision.Timeframe,
            DecisionEvaluatedAtUtc = decision.EvaluatedAtUtc,
            HorizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind,
            HorizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue,
            OutcomeState = AiShadowOutcomeState.Scored,
            OutcomeScore = 0.75m,
            RealizedDirectionality = "Long",
            ConfidenceBucket = "High",
            FutureDataAvailability = AiShadowFutureDataAvailability.Available,
            ScoredAtUtc = scoredAtUtc
        };
    }

    private static DecisionTrace CreateDecisionTrace(
        string symbol,
        string decisionReasonCode,
        string decisionSummary,
        DateTime createdAtUtc,
        Guid? strategySignalId = null)
    {
        return new DecisionTrace
        {
            Id = Guid.NewGuid(),
            StrategySignalId = strategySignalId,
            CorrelationId = $"corr-{Guid.NewGuid():N}",
            DecisionId = $"decision-{Guid.NewGuid():N}",
            UserId = "admin-monitoring-test",
            Symbol = symbol,
            Timeframe = "1m",
            StrategyVersion = "StrategyVersion:test",
            SignalType = "Exit",
            DecisionOutcome = "Allow",
            DecisionReasonType = "Allow",
            DecisionReasonCode = decisionReasonCode,
            DecisionSummary = decisionSummary,
            DecisionAtUtc = createdAtUtc,
            SnapshotJson = "{}",
            CreatedAtUtc = createdAtUtc,
            CreatedDate = createdAtUtc,
            UpdatedDate = createdAtUtc
        };
    }

    private static TradingStrategySignal CreateStrategySignal(
        Guid signalId,
        Guid strategyId,
        Guid strategyVersionId,
        StrategySignalType signalType,
        string symbol,
        DateTime generatedAtUtc)
    {
        return new TradingStrategySignal
        {
            Id = signalId,
            OwnerUserId = "admin-monitoring-test",
            TradingStrategyId = strategyId,
            TradingStrategyVersionId = strategyVersionId,
            StrategyVersionNumber = 1,
            StrategySchemaVersion = 2,
            SignalType = signalType,
            ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet,
            Symbol = symbol,
            Timeframe = "1m",
            IndicatorOpenTimeUtc = generatedAtUtc.AddMinutes(-1),
            IndicatorCloseTimeUtc = generatedAtUtc,
            IndicatorReceivedAtUtc = generatedAtUtc,
            GeneratedAtUtc = generatedAtUtc,
            IndicatorSnapshotJson = "{}",
            RuleResultSnapshotJson = "{}",
            CreatedDate = generatedAtUtc,
            UpdatedDate = generatedAtUtc
        };
    }

    private static ExecutionOrder CreateExecutionOrder(
        Guid strategySignalId,
        Guid strategyId,
        Guid strategyVersionId,
        string strategyKey,
        string symbol,
        StrategySignalType signalType,
        ExecutionOrderSide side,
        decimal quantity,
        decimal price,
        bool reduceOnly,
        DateTime createdAtUtc)
    {
        return new ExecutionOrder
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "admin-monitoring-test",
            TradingStrategyId = strategyId,
            TradingStrategyVersionId = strategyVersionId,
            StrategySignalId = strategySignalId,
            SignalType = signalType,
            BotId = Guid.Parse("474A064C-6EF8-4E8A-82C7-7F150D8BBAD2"),
            ExchangeAccountId = Guid.Parse("8F61C0E3-D082-4F28-4080-08DE97E95FBB"),
            Plane = ExchangeDataPlane.Futures,
            StrategyKey = strategyKey,
            Symbol = symbol,
            Timeframe = "1m",
            BaseAsset = symbol[..^4],
            QuoteAsset = "USDT",
            Side = side,
            OrderType = ExecutionOrderType.Market,
            Quantity = quantity,
            Price = price,
            FilledQuantity = 0m,
            AverageFillPrice = null,
            ReduceOnly = reduceOnly,
            ExecutionEnvironment = ExecutionEnvironment.BinanceTestnet,
            ExecutorKind = ExecutionOrderExecutorKind.BinanceTestnet,
            State = ExecutionOrderState.Submitted,
            IdempotencyKey = $"profit-quality-{Guid.NewGuid():N}",
            RootCorrelationId = $"corr-{Guid.NewGuid():N}",
            SubmittedToBroker = true,
            SubmittedAtUtc = createdAtUtc,
            LastStateChangedAtUtc = createdAtUtc,
            CreatedDate = createdAtUtc,
            UpdatedDate = createdAtUtc
        };
    }

    private static MarketScannerHandoffAttempt CreateBlockedHandoffAttempt(DateTime completedAtUtc, string blockerCode)
    {
        return new MarketScannerHandoffAttempt
        {
            Id = Guid.NewGuid(),
            ScanCycleId = Guid.NewGuid(),
            SelectedSymbol = "BTCUSDT",
            SelectedTimeframe = "1m",
            SelectedAtUtc = completedAtUtc,
            SelectionReason = "Top-ranked eligible candidate selected.",
            StrategyDecisionOutcome = "Persisted",
            ExecutionRequestStatus = "Blocked",
            BlockerCode = blockerCode,
            CompletedAtUtc = completedAtUtc,
            CreatedDate = completedAtUtc,
            UpdatedDate = completedAtUtc
        };
    }

    private sealed class FakeUltraDebugLogService : IUltraDebugLogService
    {
        public UltraDebugLogHealthSnapshot HealthSnapshot { get; set; } = UltraDebugLogHealthSnapshot.Empty();

        public IReadOnlyCollection<UltraDebugLogDurationOption> GetDurationOptions() => Array.Empty<UltraDebugLogDurationOption>();

        public IReadOnlyCollection<UltraDebugLogSizeLimitOption> GetLogSizeLimitOptions() => Array.Empty<UltraDebugLogSizeLimitOption>();

        public Task<UltraDebugLogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new UltraDebugLogSnapshot(false, null, null, null, null, null, null, null, false));

        public Task<UltraDebugLogHealthSnapshot> GetHealthSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(HealthSnapshot);

        public Task<UltraDebugLogSnapshot> EnableAsync(UltraDebugLogEnableRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UltraDebugLogSnapshot> DisableAsync(UltraDebugLogDisableRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UltraDebugLogTailSnapshot> SearchAsync(UltraDebugLogSearchRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UltraDebugLogExportSnapshot> ExportAsync(UltraDebugLogExportRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UltraDebugLogRetentionRunSnapshot> ApplyRetentionAsync(UltraDebugLogRetentionRunRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WriteAsync(UltraDebugLogEntry entry, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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
