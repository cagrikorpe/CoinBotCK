using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Strategies;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class AdminWorkspaceReadModelServiceTests
{
    [Fact]
    public async Task GetSupportLookupAsync_UsesRealBotAndExchangeCounts_AndOnlyReturnsActualFailures()
    {
        var now = new DateTime(2026, 3, 31, 10, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();

        var user = new ApplicationUser
        {
            Id = "usr-001",
            UserName = "alice",
            Email = "alice@coinbot.local",
            FullName = "Alice Example",
            EmailConfirmed = true,
            MfaEnabled = true,
            PreferredMfaProvider = "totp",
            MfaUpdatedAtUtc = now.AddMinutes(-30),
            TradingModeOverride = ExecutionEnvironment.Demo
        };

        var failingBotId = Guid.NewGuid();
        var healthyBotId = Guid.NewGuid();

        dbContext.Users.Add(user);
        dbContext.TradingBots.AddRange(
            new TradingBot
            {
                Id = failingBotId,
                OwnerUserId = user.Id,
                Name = "Momentum Bot",
                StrategyKey = "momentum",
                IsEnabled = true,
                OpenOrderCount = 2,
                OpenPositionCount = 1,
                CreatedDate = now.AddHours(-2),
                UpdatedDate = now.AddMinutes(-20)
            },
            new TradingBot
            {
                Id = healthyBotId,
                OwnerUserId = user.Id,
                Name = "Range Bot",
                StrategyKey = "range",
                IsEnabled = false,
                OpenOrderCount = 0,
                OpenPositionCount = 0,
                CreatedDate = now.AddHours(-1),
                UpdatedDate = now.AddMinutes(-10)
            });
        dbContext.ExchangeAccounts.Add(
            new ExchangeAccount
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user.Id,
                ExchangeName = "Binance",
                DisplayName = "Primary Binance",
                CredentialStatus = ExchangeCredentialStatus.Active,
                CredentialFingerprint = "abcd1234efgh5678",
                CreatedDate = now.AddHours(-1),
                UpdatedDate = now.AddMinutes(-15)
            });
        dbContext.ExecutionOrders.Add(
            new ExecutionOrder
            {
                Id = Guid.NewGuid(),
                OwnerUserId = user.Id,
                TradingStrategyId = Guid.NewGuid(),
                TradingStrategyVersionId = Guid.NewGuid(),
                StrategySignalId = Guid.NewGuid(),
                SignalType = StrategySignalType.Entry,
                BotId = failingBotId,
                StrategyKey = "momentum",
                Symbol = "BTCUSDT",
                Timeframe = "1m",
                BaseAsset = "BTC",
                QuoteAsset = "USDT",
                Side = ExecutionOrderSide.Buy,
                OrderType = ExecutionOrderType.Market,
                Quantity = 0.1m,
                Price = 50000m,
                FilledQuantity = 0m,
                ExecutionEnvironment = ExecutionEnvironment.Demo,
                ExecutorKind = ExecutionOrderExecutorKind.Binance,
                State = ExecutionOrderState.Failed,
                IdempotencyKey = "idem-001",
                RootCorrelationId = "corr-001",
                FailureCode = "ORDER_REJECTED",
                FailureDetail = "Position mode mismatch",
                LastStateChangedAtUtc = now.AddMinutes(-5),
                CreatedDate = now.AddMinutes(-6),
                UpdatedDate = now.AddMinutes(-5)
            });

        await dbContext.SaveChangesAsync();

        var service = new AdminWorkspaceReadModelService(
            dbContext,
            new FakeAdminMonitoringReadModelService(now),
            new FakeTradingModeResolver(),
            new FixedTimeProvider(now));

        var snapshot = await service.GetSupportLookupAsync("alice");

        var matchedUser = Assert.Single(snapshot.MatchedUsers);
        Assert.Equal(2, matchedUser.BotCount);
        Assert.Equal(1, matchedUser.ExchangeCount);

        var botError = Assert.Single(snapshot.BotErrors);
        Assert.Equal("Momentum Bot", botError.Name);
        Assert.Contains("ORDER_REJECTED", botError.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.BotErrors, item => item.Name == "Range Bot");
    }

    [Fact]
    public async Task GetStrategyAiMonitoringAsync_ProjectsTemplateMetadata_AndLatestExplainabilitySnapshot()
    {
        var now = new DateTime(2026, 4, 3, 12, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();

        var strategyId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "strategy-owner-001",
            StrategyKey = "scanner-handoff-smoke",
            DisplayName = "Scanner Handoff Smoke",
            PublishedMode = ExecutionEnvironment.Live,
            PublishedAtUtc = now.AddMinutes(-10),
            CreatedDate = now.AddHours(-1),
            UpdatedDate = now.AddMinutes(-10)
        });

        dbContext.TradingStrategyVersions.Add(new TradingStrategyVersion
        {
            Id = versionId,
            OwnerUserId = "strategy-owner-001",
            TradingStrategyId = strategyId,
            SchemaVersion = 2,
            VersionNumber = 1,
            Status = StrategyVersionStatus.Published,
            DefinitionJson = """
                             {
                               "schemaVersion": 2,
                               "metadata": {
                                 "templateKey": "rsi-reversal",
                                 "templateName": "RSI Reversal"
                               },
                               "entry": {
                                 "ruleId": "entry-mode",
                                 "ruleType": "context",
                                 "path": "context.mode",
                                 "comparison": "equals",
                                 "value": "Live",
                                 "timeframe": "1m",
                                 "weight": 20,
                                 "enabled": true
                               }
                             }
                             """,
            PublishedAtUtc = now.AddMinutes(-10),
            CreatedDate = now.AddHours(-1),
            UpdatedDate = now.AddMinutes(-10)
        });

        dbContext.TradingStrategySignalVetoes.Add(new TradingStrategySignalVeto
        {
            Id = Guid.NewGuid(),
            OwnerUserId = "strategy-owner-001",
            TradingStrategyId = strategyId,
            TradingStrategyVersionId = versionId,
            StrategyVersionNumber = 1,
            StrategySchemaVersion = 2,
            SignalType = StrategySignalType.Entry,
            ExecutionEnvironment = ExecutionEnvironment.Live,
            Symbol = "BTCUSDT",
            Timeframe = "1m",
            IndicatorOpenTimeUtc = now.AddMinutes(-1),
            IndicatorCloseTimeUtc = now,
            IndicatorReceivedAtUtc = now,
            EvaluatedAtUtc = now,
            ReasonCode = RiskVetoReasonCode.AccountEquityUnavailable,
            RiskEvaluationJson = System.Text.Json.JsonSerializer.Serialize(new CoinBot.Application.Abstractions.Strategies.StrategySignalConfidenceSnapshot(
                0,
                CoinBot.Application.Abstractions.Strategies.StrategySignalConfidenceBand.Low,
                0,
                1,
                true,
                false,
                true,
                RiskVetoReasonCode.AccountEquityUnavailable,
                false,
                "AccountEquityUnavailable")),
            CreatedDate = now,
            UpdatedDate = now
        });

        await dbContext.SaveChangesAsync();

        var service = new AdminWorkspaceReadModelService(
            dbContext,
            new FakeAdminMonitoringReadModelService(now),
            new FakeTradingModeResolver(),
            new FixedTimeProvider(now));

        var snapshot = await service.GetStrategyAiMonitoringAsync("scanner-handoff-smoke");

        var usageRow = Assert.Single(snapshot.UsageRows);
        Assert.Equal("rsi-reversal", usageRow.TemplateKey);
        Assert.Equal("RSI Reversal", usageRow.TemplateName);
        Assert.Equal("Valid", usageRow.ValidationStatusCode);
        Assert.Equal("0/100", usageRow.LatestScoreLabel);
        Assert.Equal("AccountEquityUnavailable", usageRow.LatestExplainabilitySummary);
        Assert.Contains("RiskVeto=AccountEquityUnavailable", usageRow.LatestRuleSummary, StringComparison.Ordinal);

        Assert.Contains(snapshot.TemplateCatalog, template => template.TemplateKey == "rsi-reversal" && template.ValidationStatusCode == "Valid");
        Assert.Equal("scanner-handoff-smoke", snapshot.LatestExplainability.StrategyKey);
        Assert.Equal("BTCUSDT", snapshot.LatestExplainability.Symbol);
        Assert.Equal("1m", snapshot.LatestExplainability.Timeframe);
        Assert.Equal("Vetoed:AccountEquityUnavailable", snapshot.LatestExplainability.Outcome);
        Assert.Equal("0/100", snapshot.LatestExplainability.ScoreLabel);
        Assert.Equal("AccountEquityUnavailable", snapshot.LatestExplainability.Summary);
        Assert.Contains("RiskVeto=AccountEquityUnavailable", snapshot.LatestExplainability.RuleSummary, StringComparison.Ordinal);
        Assert.Equal("RSI Reversal", snapshot.LatestExplainability.TemplateName);
    }

    [Fact]
    public async Task GetStrategyAiMonitoringAsync_ProjectsTemplateRevisionRuntimeVersion_AndLifecycleToken()
    {
        var now = new DateTime(2026, 4, 5, 12, 0, 0, DateTimeKind.Utc);
        await using var dbContext = CreateDbContext();
        var parser = new StrategyRuleParser();
        var validator = new StrategyDefinitionValidator();
        var templateCatalog = new StrategyTemplateCatalogService(parser, validator, dbContext);

        var template = await templateCatalog.CreateCustomAsync(
            "strategy-owner-002",
            "ops-template",
            "Ops Template",
            "Ops lifecycle template.",
            "Custom",
            CreateStrategyDefinition("ops-template", "Ops Template", revision: 1, timeframe: "30m"));
        var revisedTemplate = await templateCatalog.ReviseAsync(
            "ops-template",
            "Ops Template",
            "Ops lifecycle template revised.",
            "Custom",
            CreateStrategyDefinition("ops-template", "Ops Template", revision: 2, timeframe: "30m"));

        var strategyId = Guid.NewGuid();
        var version1Id = Guid.NewGuid();
        var version2Id = Guid.NewGuid();
        dbContext.TradingStrategies.Add(new TradingStrategy
        {
            Id = strategyId,
            OwnerUserId = "strategy-owner-002",
            StrategyKey = "ops-lifecycle",
            DisplayName = "Ops Lifecycle",
            UsesExplicitVersionLifecycle = true,
            ActiveTradingStrategyVersionId = version1Id,
            ActiveVersionActivatedAtUtc = now.AddMinutes(-2),
            ActivationConcurrencyToken = [(byte)1, (byte)2, (byte)3, (byte)4],
            PublishedMode = ExecutionEnvironment.Live,
            PublishedAtUtc = now.AddMinutes(-5),
            CreatedDate = now.AddHours(-1),
            UpdatedDate = now.AddMinutes(-1)
        });
        dbContext.TradingStrategyVersions.AddRange(
            new TradingStrategyVersion
            {
                Id = version1Id,
                OwnerUserId = "strategy-owner-002",
                TradingStrategyId = strategyId,
                SchemaVersion = 2,
                VersionNumber = 1,
                Status = StrategyVersionStatus.Published,
                DefinitionJson = CreateStrategyDefinition("ops-template", "Ops Template", revision: 1, timeframe: "30m"),
                PublishedAtUtc = now.AddMinutes(-4),
                CreatedDate = now.AddHours(-1),
                UpdatedDate = now.AddMinutes(-4)
            },
            new TradingStrategyVersion
            {
                Id = version2Id,
                OwnerUserId = "strategy-owner-002",
                TradingStrategyId = strategyId,
                SchemaVersion = 2,
                VersionNumber = 2,
                Status = StrategyVersionStatus.Published,
                DefinitionJson = CreateStrategyDefinition("ops-template", "Ops Template", revision: 2, timeframe: "30m"),
                PublishedAtUtc = now.AddMinutes(-1),
                CreatedDate = now.AddMinutes(-2),
                UpdatedDate = now.AddMinutes(-1)
            });

        await dbContext.SaveChangesAsync();

        var service = new AdminWorkspaceReadModelService(
            dbContext,
            new FakeAdminMonitoringReadModelService(now),
            new FakeTradingModeResolver(),
            new FixedTimeProvider(now),
            parser,
            validator,
            templateCatalog);

        var snapshot = await service.GetStrategyAiMonitoringAsync("ops-lifecycle");

        var usageRow = Assert.Single(snapshot.UsageRows);
        Assert.Equal("v1", usageRow.RuntimeVersionLabel);
        Assert.Equal("v2", usageRow.LatestVersionLabel);
        Assert.Equal("r1", usageRow.RuntimeTemplateRevisionLabel);
        Assert.Equal("r2", usageRow.LatestTemplateRevisionLabel);
        Assert.NotEqual("n/a", usageRow.LifecycleTokenLabel);
        Assert.Contains("Runtime=v1; Latest=v2", usageRow.Note, StringComparison.Ordinal);
        Assert.Contains(snapshot.TemplateCatalog, templateRow =>
            templateRow.TemplateKey == template.TemplateKey &&
            templateRow.ActiveRevisionNumber == revisedTemplate.ActiveRevisionNumber &&
            templateRow.LatestRevisionNumber == revisedTemplate.LatestRevisionNumber &&
            templateRow.TemplateSource == "Custom");
    }
    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class FakeAdminMonitoringReadModelService(DateTime now) : IAdminMonitoringReadModelService
    {
        public Task<MonitoringDashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(MonitoringDashboardSnapshot.Empty(now));
        }
    }

    private sealed class FakeTradingModeResolver : ITradingModeResolver
    {
        public Task<TradingModeResolution> ResolveAsync(
            TradingModeResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new TradingModeResolution(
                    ExecutionEnvironment.Demo,
                    ExecutionEnvironment.Demo,
                    null,
                    null,
                    ExecutionEnvironment.Demo,
                    TradingModeResolutionSource.UserOverride,
                    "Demo user override",
                    false));
        }
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private static string CreateStrategyDefinition(string templateKey, string templateName, int revision, string timeframe)
    {
        return
            $$"""
            {
              "schemaVersion": 2,
              "metadata": {
                "templateKey": "{{templateKey}}",
                "templateName": "{{templateName}}",
                "templateRevisionNumber": {{revision}},
                "templateSource": "Custom"
              },
              "entry": {
                "path": "context.mode",
                "comparison": "equals",
                "value": "Live",
                "ruleId": "entry-mode",
                "ruleType": "context",
                "timeframe": "{{timeframe}}",
                "weight": 20,
                "enabled": true
              }
            }
            """;
    }
}


