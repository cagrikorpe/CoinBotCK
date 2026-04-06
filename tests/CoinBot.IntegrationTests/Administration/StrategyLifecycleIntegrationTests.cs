using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Ai;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Risk;
using CoinBot.Infrastructure.Strategies;
using CoinBot.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoinBot.IntegrationTests.Administration;

public sealed class StrategyLifecycleIntegrationTests
{
    [Fact]
    public async Task CustomTemplate_PublishActivate_RuntimeSignal_AndAdminProjection_OnSqlServer()
    {
        var databaseName = $"CoinBotStrategyLifecycleInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            var strategyId = Guid.NewGuid();
            dbContext.Users.Add(CreateUser("strategy-user"));
            dbContext.TradingStrategies.Add(new TradingStrategy
            {
                Id = strategyId,
                OwnerUserId = "strategy-user",
                StrategyKey = "strategy-lifecycle",
                DisplayName = "Strategy Lifecycle",
                PromotionState = StrategyPromotionState.DemoPublished,
                PublishedMode = ExecutionEnvironment.Demo,
                PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-5)
            });
            dbContext.RiskProfiles.Add(new RiskProfile
            {
                OwnerUserId = "strategy-user",
                ProfileName = "Balanced",
                MaxDailyLossPercentage = 5m,
                MaxPositionSizePercentage = 80m,
                MaxLeverage = 2m
            });
            dbContext.DemoWallets.Add(new DemoWallet
            {
                OwnerUserId = "strategy-user",
                Asset = "USDT",
                AvailableBalance = 10000m,
                ReservedBalance = 0m,
                LastActivityAtUtc = nowUtc.UtcDateTime
            });
            await dbContext.SaveChangesAsync();

            var parser = new StrategyRuleParser();
            var validator = new StrategyDefinitionValidator();
            var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
            var templateCatalog = new StrategyTemplateCatalogService(parser, validator, dbContext, auditLogService);
            var versionService = new StrategyVersionService(
                dbContext,
                parser,
                new FixedTimeProvider(nowUtc),
                validator,
                templateCatalog,
                auditLogService);
            var signalService = CreateSignalService(dbContext, nowUtc);

            var template = await templateCatalog.CreateCustomAsync(
                "strategy-user",
                "demo-rsi-template",
                "Demo RSI Template",
                "Custom demo template.",
                "Custom",
                CreateDefinitionJson("Demo", 35m, 100));
            var revisedTemplate = await templateCatalog.ReviseAsync(
                "demo-rsi-template",
                "Demo RSI Template",
                "Custom demo template revised.",
                "Custom",
                CreateDefinitionJson("Demo", 32m, 100, timeframe: "30m", includeLatencyRule: true));
            var draft = await versionService.CreateDraftFromTemplateAsync(strategyId, revisedTemplate.TemplateKey);
            var published = await versionService.PublishAsync(draft.StrategyVersionId);
            var result = await signalService.GenerateAsync(
                new GenerateStrategySignalsRequest(
                    published.StrategyVersionId,
                    CreateContext(ExecutionEnvironment.Demo, nowUtc.UtcDateTime, sampleCount: 120, rsiValue: 28m, timeframe: "30m")));
            var readModelService = new AdminWorkspaceReadModelService(
                dbContext,
                new FakeAdminMonitoringReadModelService(nowUtc.UtcDateTime),
                new FakeTradingModeResolver(),
                new FixedTimeProvider(nowUtc),
                parser,
                validator,
                templateCatalog);
            var snapshot = await readModelService.GetStrategyAiMonitoringAsync("strategy-lifecycle");
            var persistedStrategy = await dbContext.TradingStrategies.AsNoTracking().SingleAsync(entity => entity.Id == strategyId);
            var decisionTrace = await dbContext.DecisionTraces.AsNoTracking().SingleAsync();
            var auditActions = await dbContext.AuditLogs
                .AsNoTracking()
                .Where(entity => entity.Target == "strategy-lifecycle")
                .Select(entity => entity.Action)
                .ToListAsync();

            Assert.True(published.IsActive);
            Assert.Equal(published.StrategyVersionId, persistedStrategy.ActiveTradingStrategyVersionId);
            Assert.True(persistedStrategy.UsesExplicitVersionLifecycle);
            Assert.Single(result.Signals);
            Assert.Empty(result.Vetoes);
            Assert.Contains("\"templateKey\":\"demo-rsi-template\"", decisionTrace.SnapshotJson, StringComparison.Ordinal);
            Assert.Contains("\"templateRevisionNumber\":2", decisionTrace.SnapshotJson, StringComparison.Ordinal);
            Assert.Equal(2, published.TemplateRevisionNumber);
            Assert.Contains("\"outcome\":\"EntryMatched\"", decisionTrace.SnapshotJson, StringComparison.Ordinal);
            Assert.Contains("Strategy.Version.Published", auditActions);
            Assert.Contains("Strategy.Version.Activated", auditActions);
            var usageRow = Assert.Single(snapshot.UsageRows);
            Assert.Equal("demo-rsi-template", usageRow.TemplateKey);
            Assert.Equal("r2", usageRow.RuntimeTemplateRevisionLabel);
            Assert.Contains("Runtime=v1", usageRow.Note, StringComparison.Ordinal);
            Assert.Contains(snapshot.TemplateCatalog, item => item.TemplateKey == "demo-rsi-template" && item.ActiveRevisionNumber == 2 && item.LatestRevisionNumber == 2 && item.TemplateSource == "Custom");
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ActiveVersionSwap_SeparatesPersistedNoSignalAndRiskVeto_Outcomes_OnSqlServer()
    {
        var databaseName = $"CoinBotStrategyLifecycleSwapInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);
        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        try
        {
            var strategyId = Guid.NewGuid();
            dbContext.Users.Add(CreateUser("strategy-swap-user"));
            dbContext.TradingStrategies.Add(new TradingStrategy
            {
                Id = strategyId,
                OwnerUserId = "strategy-swap-user",
                StrategyKey = "strategy-swap",
                DisplayName = "Strategy Swap",
                PromotionState = StrategyPromotionState.DemoPublished,
                PublishedMode = ExecutionEnvironment.Demo,
                PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-5)
            });
            dbContext.RiskProfiles.Add(new RiskProfile
            {
                OwnerUserId = "strategy-swap-user",
                ProfileName = "Balanced",
                MaxDailyLossPercentage = 5m,
                MaxPositionSizePercentage = 80m,
                MaxLeverage = 2m
            });
            dbContext.DemoWallets.Add(new DemoWallet
            {
                OwnerUserId = "strategy-swap-user",
                Asset = "USDT",
                AvailableBalance = 10000m,
                ReservedBalance = 0m,
                LastActivityAtUtc = nowUtc.UtcDateTime
            });
            await dbContext.SaveChangesAsync();

            var parser = new StrategyRuleParser();
            var validator = new StrategyDefinitionValidator();
            var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
            var versionService = new StrategyVersionService(
                dbContext,
                parser,
                new FixedTimeProvider(nowUtc),
                validator,
                new StrategyTemplateCatalogService(parser, validator, dbContext, auditLogService),
                auditLogService);
            var signalService = CreateSignalService(dbContext, nowUtc);

            var version1 = await versionService.PublishAsync(
                (await versionService.CreateDraftAsync(strategyId, CreateDefinitionJson("Demo", 35m, 100))).StrategyVersionId);
            var version2 = await versionService.PublishAsync(
                (await versionService.CreateDraftAsync(strategyId, CreateDefinitionJson("Demo", 10m, 100))).StrategyVersionId);

            await versionService.ActivateAsync(version1.StrategyVersionId);
            var persistedResult = await signalService.GenerateAsync(
                new GenerateStrategySignalsRequest(
                    version1.StrategyVersionId,
                    CreateContext(ExecutionEnvironment.Demo, nowUtc.UtcDateTime, sampleCount: 120, rsiValue: 28m)));

            await versionService.ActivateAsync(version2.StrategyVersionId);
            var inactiveVersionException = await Assert.ThrowsAsync<InvalidOperationException>(() => signalService.GenerateAsync(
                new GenerateStrategySignalsRequest(
                    version1.StrategyVersionId,
                    CreateContext(ExecutionEnvironment.Demo, nowUtc.UtcDateTime.AddMinutes(1), sampleCount: 120, rsiValue: 28m))));
            var noSignalResult = await signalService.GenerateAsync(
                new GenerateStrategySignalsRequest(
                    version2.StrategyVersionId,
                    CreateContext(ExecutionEnvironment.Demo, nowUtc.UtcDateTime.AddMinutes(1), sampleCount: 120, rsiValue: 28m)));

            dbContext.DemoLedgerTransactions.Add(new DemoLedgerTransaction
            {
                OwnerUserId = "strategy-swap-user",
                OperationId = Guid.NewGuid().ToString("N"),
                TransactionType = DemoLedgerTransactionType.FillApplied,
                PositionScopeKey = "risk-position",
                Symbol = "BTCUSDT",
                QuoteAsset = "USDT",
                RealizedPnlDelta = -750m,
                OccurredAtUtc = nowUtc.UtcDateTime.AddMinutes(1)
            });
            await dbContext.SaveChangesAsync();
            await versionService.ActivateAsync(version1.StrategyVersionId);
            var vetoResult = await signalService.GenerateAsync(
                new GenerateStrategySignalsRequest(
                    version1.StrategyVersionId,
                    CreateContext(ExecutionEnvironment.Demo, nowUtc.UtcDateTime.AddMinutes(2), sampleCount: 120, rsiValue: 28m)));

            var decisionTraces = await dbContext.DecisionTraces
                .AsNoTracking()
                .OrderBy(entity => entity.CreatedAtUtc)
                .ToListAsync();
            var decisionOutcomes = decisionTraces
                .Select(entity => entity.DecisionOutcome)
                .ToList();
            var vetoTrace = Assert.Single(decisionTraces.Where(entity => entity.DecisionOutcome == "Vetoed"));

            Assert.Single(persistedResult.Signals);
            Assert.Empty(noSignalResult.Signals);
            Assert.Empty(noSignalResult.Vetoes);
            Assert.Contains("active", inactiveVersionException.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(vetoResult.Vetoes);
            Assert.Contains("Persisted", decisionOutcomes);
            Assert.Contains("NoSignalCandidate", decisionOutcomes);
            Assert.Contains("Vetoed", decisionOutcomes);
            Assert.Equal("RiskVeto", vetoTrace.DecisionReasonType);
            Assert.Equal("DailyLossLimitBreached", vetoTrace.DecisionReasonCode);
            Assert.Contains("Reason=DailyLossLimitBreached", vetoTrace.DecisionSummary, StringComparison.Ordinal);
            Assert.Equal(nowUtc.UtcDateTime, vetoTrace.DecisionAtUtc);
            Assert.Equal(1, await dbContext.TradingStrategySignals.CountAsync());
            Assert.Equal(1, await dbContext.TradingStrategySignalVetoes.CountAsync());
        }
        finally
        {
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task ConcurrentActivate_AllowsSingleWinner_AndRejectsStaleRequest_OnSqlServer()
    {
        var databaseName = $"CoinBotStrategyLifecycleConcurrencyInt_{Guid.NewGuid():N}";
        var connectionString = SqlServerIntegrationDatabase.ResolveConnectionString(databaseName);
        var nowUtc = new DateTimeOffset(2026, 4, 5, 12, 0, 0, TimeSpan.Zero);
        await using var setupContext = CreateDbContext(connectionString);
        await setupContext.Database.EnsureDeletedAsync();
        await setupContext.Database.EnsureCreatedAsync();

        try
        {
            var strategyId = Guid.NewGuid();
            var version1Id = Guid.NewGuid();
            var version2Id = Guid.NewGuid();
            setupContext.Users.Add(CreateUser("strategy-concurrency-user"));
            setupContext.TradingStrategies.Add(new TradingStrategy
            {
                Id = strategyId,
                OwnerUserId = "strategy-concurrency-user",
                StrategyKey = "strategy-concurrency",
                DisplayName = "Strategy Concurrency",
                PromotionState = StrategyPromotionState.DemoPublished,
                PublishedMode = ExecutionEnvironment.Demo,
                PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-5)
            });
            setupContext.TradingStrategyVersions.AddRange(
                new TradingStrategyVersion
                {
                    Id = version1Id,
                    OwnerUserId = "strategy-concurrency-user",
                    TradingStrategyId = strategyId,
                    SchemaVersion = 2,
                    VersionNumber = 1,
                    Status = StrategyVersionStatus.Published,
                    DefinitionJson = CreateDefinitionJson("Demo", 35m, 100),
                    PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-4)
                },
                new TradingStrategyVersion
                {
                    Id = version2Id,
                    OwnerUserId = "strategy-concurrency-user",
                    TradingStrategyId = strategyId,
                    SchemaVersion = 2,
                    VersionNumber = 2,
                    Status = StrategyVersionStatus.Published,
                    DefinitionJson = CreateDefinitionJson("Demo", 20m, 100),
                    PublishedAtUtc = nowUtc.UtcDateTime.AddMinutes(-3)
                });
            await setupContext.SaveChangesAsync();

            var activationToken = Convert.ToBase64String((await setupContext.TradingStrategies.AsNoTracking().SingleAsync(entity => entity.Id == strategyId)).ActivationConcurrencyToken);
            var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<(bool Succeeded, Guid? ActiveVersionId, string? Error)> RunActivationAsync(Guid targetVersionId)
            {
                await using var dbContext = CreateDbContext(connectionString);
                var parser = new StrategyRuleParser();
                var validator = new StrategyDefinitionValidator();
                var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
                var service = new StrategyVersionService(
                    dbContext,
                    parser,
                    new FixedTimeProvider(nowUtc),
                    validator,
                    new StrategyTemplateCatalogService(parser, validator, dbContext, auditLogService),
                    auditLogService);

                await startSignal.Task;

                try
                {
                    var snapshot = await service.ActivateAsync(targetVersionId, activationToken);
                    return (true, snapshot.StrategyVersionId, null);
                }
                catch (Exception exception)
                {
                    return (false, null, exception.Message);
                }
            }

            var activateV1Task = RunActivationAsync(version1Id);
            var activateV2Task = RunActivationAsync(version2Id);
            startSignal.SetResult();

            var results = await Task.WhenAll(activateV1Task, activateV2Task);
            await using var assertContext = CreateDbContext(connectionString);
            var persistedStrategy = await assertContext.TradingStrategies.AsNoTracking().SingleAsync(entity => entity.Id == strategyId);
            var activationAudits = await assertContext.AuditLogs
                .AsNoTracking()
                .Where(entity => entity.Target == "strategy-concurrency" && entity.Action == "Strategy.Version.Activated")
                .ToListAsync();

            Assert.Equal(1, results.Count(result => result.Succeeded));
            Assert.Equal(1, results.Count(result => !result.Succeeded && result.Error is not null && result.Error.Contains("stale", StringComparison.OrdinalIgnoreCase)));
            Assert.NotNull(persistedStrategy.ActiveTradingStrategyVersionId);
            Assert.Contains(persistedStrategy.ActiveTradingStrategyVersionId!.Value, new[] { version1Id, version2Id });
            Assert.Single(activationAudits);
        }
        finally
        {
            await setupContext.Database.EnsureDeletedAsync();
        }
    }

    private static StrategySignalService CreateSignalService(ApplicationDbContext dbContext, DateTimeOffset nowUtc)
    {
        var correlationContextAccessor = new CorrelationContextAccessor();
        var fixedTimeProvider = new FixedTimeProvider(nowUtc);

        return new StrategySignalService(
            dbContext,
            new StrategyEvaluatorService(new StrategyRuleParser(), new StrategyDefinitionValidator()),
            new RiskPolicyEvaluator(
                dbContext,
                fixedTimeProvider,
                NullLogger<RiskPolicyEvaluator>.Instance),
            new TraceService(
                dbContext,
                correlationContextAccessor,
                fixedTimeProvider),
            correlationContextAccessor,
            CreateAiSignalEvaluator(nowUtc),
            Options.Create(new AiSignalOptions()),
            fixedTimeProvider,
            NullLogger<StrategySignalService>.Instance);
    }

    private static IAiSignalEvaluator CreateAiSignalEvaluator(DateTimeOffset nowUtc)
    {
        return new AiSignalEvaluator(
            [new DeterministicStubAiSignalProviderAdapter(), new OfflineAiSignalProviderAdapter(), new OpenAiSignalProviderAdapter(), new GeminiAiSignalProviderAdapter()],
            Options.Create(new AiSignalOptions()),
            new FixedTimeProvider(nowUtc),
            NullLogger<AiSignalEvaluator>.Instance);
    }

    private static StrategyEvaluationContext CreateContext(
        ExecutionEnvironment mode,
        DateTime closeTimeUtc,
        int sampleCount,
        decimal rsiValue,
        string timeframe = "1m")
    {
        return new StrategyEvaluationContext(
            mode,
            new StrategyIndicatorSnapshot(
                Symbol: "BTCUSDT",
                Timeframe: timeframe,
                OpenTimeUtc: closeTimeUtc.AddMinutes(-1),
                CloseTimeUtc: closeTimeUtc,
                ReceivedAtUtc: closeTimeUtc.AddSeconds(1),
                SampleCount: sampleCount,
                RequiredSampleCount: 120,
                State: IndicatorDataState.Ready,
                DataQualityReasonCode: DegradedModeReasonCode.None,
                Rsi: new RelativeStrengthIndexSnapshot(14, IsReady: true, Value: rsiValue),
                Macd: new MovingAverageConvergenceDivergenceSnapshot(12, 26, 9, true, 1.4m, 1.1m, 0.3m),
                Bollinger: new BollingerBandsSnapshot(20, 2m, true, 62000m, 62500m, 61500m, 250m),
                Source: "integration-test"));
    }

    private static string CreateDefinitionJson(string mode, decimal rsiThreshold, int minimumSampleCount, string timeframe = "1m", bool includeLatencyRule = false)
    {
        var riskBlock = includeLatencyRule
            ? $$"""
              "risk": {
                "operator": "all",
                "ruleId": "risk-root",
                "ruleType": "group",
                "timeframe": "{{timeframe}}",
                "weight": 1,
                "enabled": true,
                "rules": [
                  {
                    "path": "indicator.sampleCount",
                    "comparison": "greaterThanOrEqual",
                    "value": {{minimumSampleCount}},
                    "ruleId": "risk-sample-count",
                    "ruleType": "data-quality",
                    "timeframe": "{{timeframe}}",
                    "weight": 10,
                    "enabled": true
                  },
                  {
                    "ruleId": "risk-latency-window",
                    "ruleType": "data-quality",
                    "path": "indicator.latencySeconds",
                    "comparison": "between",
                    "value": "0..5",
                    "timeframe": "{{timeframe}}",
                    "weight": 5,
                    "enabled": true
                  }
                ]
              }
              """
            : $$"""
              "risk": {
                "path": "indicator.sampleCount",
                "comparison": "greaterThanOrEqual",
                "value": {{minimumSampleCount}},
                "ruleId": "risk-sample-count",
                "ruleType": "data-quality",
                "timeframe": "{{timeframe}}",
                "weight": 10,
                "enabled": true
              }
              """;

        return
            $$"""
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "custom-runtime",
                "templateName": "Custom Runtime"
              },
              "entry": {
                "operator": "all",
                "ruleId": "entry-root",
                "ruleType": "group",
                "timeframe": "{{timeframe}}",
                "weight": 1,
                "enabled": true,
                "rules": [
                  {
                    "ruleId": "entry-mode",
                    "ruleType": "context",
                    "path": "context.mode",
                    "comparison": "equals",
                    "value": "{{mode}}",
                    "timeframe": "{{timeframe}}",
                    "weight": 10,
                    "enabled": true
                  },
                  {
                    "ruleId": "entry-rsi",
                    "ruleType": "rsi",
                    "path": "indicator.rsi.value",
                    "comparison": "lessThanOrEqual",
                    "value": {{rsiThreshold}},
                    "timeframe": "{{timeframe}}",
                    "weight": 20,
                    "enabled": true
                  }
                ]
              },
              {{riskBlock}}
            }
            """;
    }

    private static ApplicationUser CreateUser(string userId)
    {
        return new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            NormalizedUserName = userId.ToUpperInvariant(),
            Email = $"{userId}@coinbot.test",
            NormalizedEmail = $"{userId.ToUpperInvariant()}@COINBOT.TEST",
            FullName = userId,
            EmailConfirmed = true
        };
    }

    private static ApplicationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class FakeAdminMonitoringReadModelService(DateTime nowUtc) : IAdminMonitoringReadModelService
    {
        public Task<MonitoringDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MonitoringDashboardSnapshot.Empty(nowUtc));
        }
    }

    private sealed class FakeTradingModeResolver : ITradingModeResolver
    {
        public Task<TradingModeResolution> ResolveAsync(
            TradingModeResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TradingModeResolution(
                ExecutionEnvironment.Demo,
                null,
                null,
                null,
                ExecutionEnvironment.Demo,
                TradingModeResolutionSource.GlobalDefault,
                "integration-test",
                false));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}






