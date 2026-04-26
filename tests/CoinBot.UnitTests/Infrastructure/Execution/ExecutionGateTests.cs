using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.UnitTests.Infrastructure.Mfa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;


namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class ExecutionGateTests
{
    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenGlobalSwitchConfigurationIsMissing()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-100");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-01",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-001",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Pre-dispatch gate",
                    CorrelationId: "corr-101")));

        var auditLog = await harness.DbContext.AuditLogs.SingleAsync();

        Assert.Equal(ExecutionGateBlockedReason.SwitchConfigurationMissing, exception.Reason);
        Assert.Equal("worker-01", auditLog.Actor);
        Assert.Equal("TradeExecution.Dispatch", auditLog.Action);
        Assert.Equal("Blocked:SwitchConfigurationMissing", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
        Assert.Empty(await harness.DbContext.GlobalExecutionSwitches.ToListAsync());
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_AllowsPilotRequest_WhenGlobalSwitchIsDisarmed_AndBypassIsEnabled()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            useTestnetEndpoints: true,
            pilotOptions: new BotExecutionPilotOptions
            {
                Enabled = true,
                PilotActivationEnabled = true,
                AllowGlobalSwitchBypass = true,
                PrivatePlaneFreshnessThresholdSeconds = 120
            });
        var botId = Guid.NewGuid();
        var exchangeAccountId = Guid.NewGuid();
        await PrimeFreshMarketDataAsync(harness, "corr-pilot-bypass-1");
        harness.DbContext.ApiCredentialValidations.Add(new ApiCredentialValidation
        {
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = "user-pilot-bypass",
            ValidationStatus = "Valid",
            EnvironmentScope = "Testnet",
            IsKeyValid = true,
            CanTrade = true,
            SupportsFutures = true,
            IsEnvironmentMatch = true,
            ValidatedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime
        });
        harness.DbContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
        {
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = "user-pilot-bypass",
            Plane = ExchangeDataPlane.Futures,
            PrivateStreamConnectionState = ExchangePrivateStreamConnectionState.Connected,
            DriftStatus = ExchangeStateDriftStatus.InSync,
            LastPrivateStreamEventAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime,
            LastBalanceSyncedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime,
            LastPositionSyncedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime,
            LastStateReconciledAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime
        });
        await harness.DbContext.SaveChangesAsync();

        var snapshot = await harness.ExecutionGate.EnsureExecutionAllowedAsync(
            new ExecutionGateRequest(
                Actor: "system:bot-worker",
                Action: "TradeExecution.Dispatch",
                Target: "bot-pilot-bypass",
                Environment: ExecutionEnvironment.Live,
                Context: "DevelopmentFuturesTestnetPilot=True | PilotActivationEnabled=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                CorrelationId: "corr-pilot-bypass-2",
                UserId: "user-pilot-bypass",
                BotId: botId,
                ExchangeAccountId: exchangeAccountId,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                Plane: ExchangeDataPlane.Futures));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.False(snapshot.IsPersisted);
        Assert.Equal("Allowed", auditLog.Outcome);
        Assert.DoesNotContain("TradeMasterDisarmed", auditLog.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenTradeMasterIsDisarmed()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-200");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-10",
            liveApproval: new TradingModeLiveApproval("chg-201"),
            context: "Prepare live mode",
            correlationId: "corr-201");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-02",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-002",
                    Environment: ExecutionEnvironment.Live,
                    Context: "Live dispatch attempt",
                    CorrelationId: "corr-202")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.TradeMasterDisarmed, exception.Reason);
        Assert.Equal("Blocked:TradeMasterDisarmed", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Live), auditLog.Environment);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksLiveExecutionWhenDemoModeIsEnabled()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-300");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-11",
            context: "Demo runtime armed",
            correlationId: "corr-301");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-03",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-003",
                    Environment: ExecutionEnvironment.Live,
                    Context: "Live dispatch attempt",
                    CorrelationId: "corr-302")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.LiveExecutionBlockedByDemoMode, exception.Reason);
        Assert.Equal("Blocked:LiveExecutionClosedByDemoMode", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Live), auditLog.Environment);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_AllowsDemoExecution_WhenTradeMasterIsArmed()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-400");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-12",
            context: "Demo execution window open",
            correlationId: "corr-401");

        var snapshot = await harness.ExecutionGate.EnsureExecutionAllowedAsync(
            new ExecutionGateRequest(
                Actor: "worker-04",
                Action: "TradeExecution.Dispatch",
                Target: "bot-004",
                Environment: ExecutionEnvironment.Demo,
                Context: "Demo dispatch attempt",
                CorrelationId: "corr-402"));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");
        var decisionTrace = await harness.DbContext.DecisionTraces.SingleAsync();

        Assert.True(snapshot.IsPersisted);
        Assert.True(snapshot.IsTradeMasterArmed);
        Assert.True(snapshot.DemoModeEnabled);
        Assert.Equal("Allowed", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
        Assert.Contains("DecisionOutcome=Allow", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonType=Allow", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonCode=Allowed", auditLog.Context, StringComparison.Ordinal);
        Assert.Equal("Allow", decisionTrace.DecisionOutcome);
        Assert.Equal("Allow", decisionTrace.DecisionReasonType);
        Assert.Equal("Allowed", decisionTrace.DecisionReasonCode);
        Assert.Equal("Execution decision allowed the request.", decisionTrace.DecisionSummary);
        Assert.NotNull(decisionTrace.DecisionAtUtc);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BootstrapsDemoSessionWallet_WhenDemoRequestHasNoSession()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-demo-bootstrap-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-12",
            context: "Demo execution window open",
            correlationId: "corr-demo-bootstrap-2");

        var snapshot = await harness.ExecutionGate.EnsureExecutionAllowedAsync(
            new ExecutionGateRequest(
                Actor: "system:market-scanner",
                Action: "MarketScanner.Handoff",
                Target: "MarketScannerCandidate/demo-bootstrap",
                Environment: ExecutionEnvironment.Demo,
                Context: "Demo scanner handoff",
                CorrelationId: "corr-demo-bootstrap-3",
                UserId: "user-demo-bootstrap",
                Symbol: "BTCUSDT",
                Timeframe: "1m"));

        var wallet = await harness.DbContext.DemoWallets.SingleAsync(entity => entity.OwnerUserId == "user-demo-bootstrap");
        var session = await harness.DbContext.DemoSessions.SingleAsync(entity => entity.OwnerUserId == "user-demo-bootstrap");
        var sessionAudit = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "DemoSession.ResetApplied");
        Assert.True(snapshot.DemoModeEnabled);
        Assert.Equal(DemoSessionState.Active, session.State);
        Assert.Equal(DemoConsistencyStatus.InSync, session.ConsistencyStatus);
        Assert.Equal("USDT", wallet.Asset);
        Assert.Equal(10000m, wallet.AvailableBalance);
        Assert.Equal(0m, wallet.ReservedBalance);
        Assert.Equal("system:market-scanner", sessionAudit.Actor);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), sessionAudit.Environment);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenDemoSessionDriftIsDetected()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-450");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-12",
            context: "Demo execution window open",
            correlationId: "corr-451");

        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-drift",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 1000m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.Unknown,
            StartedAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(-1)
        });
        harness.DbContext.DemoWallets.Add(new DemoWallet
        {
            OwnerUserId = "user-drift",
            Asset = "USDT",
            AvailableBalance = 1000m,
            ReservedBalance = 0m,
            LastActivityAtUtc = harness.TimeProvider.GetUtcNow().UtcDateTime
        });
        await harness.DbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-04b",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-004b",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Demo dispatch attempt",
                    CorrelationId: "corr-452",
                    UserId: "user-drift")));

        var session = await harness.DbContext.DemoSessions.SingleAsync(entity => entity.OwnerUserId == "user-drift");
        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Outcome == "Blocked:DemoSessionDriftDetected");

        Assert.Equal(ExecutionGateBlockedReason.DemoSessionDriftDetected, exception.Reason);
        Assert.Contains("DecisionSourceLayer=DemoSessionConsistency", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ComparedState=LedgerSnapshotsVsDemoPortfolioProjection", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ConsistencyTolerance=0.00000001", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ComputedDriftMs=not_applicable", exception.Message, StringComparison.Ordinal);
        Assert.Equal(DemoConsistencyStatus.DriftDetected, session.ConsistencyStatus);
        Assert.Contains("EvaluatedAtUtc=", session.LastDriftSummary, StringComparison.Ordinal);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
        Assert.Contains("DemoSessionCheckedAtUtc=", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DemoDrift=WalletMismatches=1", auditLog.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_AllowsWhenMissingDemoPositionProjectionIsRecovered()
    {
        await using var harness = CreateHarness();
        var botId = Guid.NewGuid();
        var positionScopeKey = $"bot:{botId:N}";
        var now = harness.TimeProvider.GetUtcNow().UtcDateTime;
        await PrimeFreshMarketDataAsync(harness, "corr-demo-recovery-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-demo-recovery",
            context: "Demo execution window open",
            correlationId: "corr-demo-recovery-2");

        harness.DbContext.TradingBots.Add(new TradingBot
        {
            Id = botId,
            OwnerUserId = "user-demo-recovery",
            Name = "Demo Bot",
            StrategyKey = "demo-recovery",
            Symbol = "SOLUSDT",
            OpenOrderCount = 0,
            OpenPositionCount = 0
        });
        harness.DbContext.DemoSessions.Add(new DemoSession
        {
            OwnerUserId = "user-demo-recovery",
            SequenceNumber = 1,
            SeedAsset = "USDT",
            SeedAmount = 10000m,
            State = DemoSessionState.Active,
            ConsistencyStatus = DemoConsistencyStatus.DriftDetected,
            StartedAtUtc = now.AddMinutes(-10)
        });
        harness.DbContext.DemoLedgerTransactions.Add(new DemoLedgerTransaction
        {
            OwnerUserId = "user-demo-recovery",
            OperationId = "mark-price:demo-recovery",
            TransactionType = DemoLedgerTransactionType.MarkPriceUpdated,
            BotId = botId,
            PositionScopeKey = positionScopeKey,
            Symbol = "SOLUSDT",
            BaseAsset = "SOL",
            QuoteAsset = "USDT",
            PositionKind = DemoPositionKind.Futures,
            MarginMode = DemoMarginMode.Cross,
            Leverage = 1m,
            PositionQuantityAfter = 0.06m,
            PositionCostBasisAfter = 5.214m,
            PositionAverageEntryPriceAfter = 86.9m,
            CumulativeRealizedPnlAfter = 0m,
            UnrealizedPnlAfter = 0.012m,
            CumulativeFeesInQuoteAfter = 0.003m,
            NetFundingInQuoteAfter = 0m,
            LastPriceAfter = 87.1m,
            MarkPriceAfter = 87.2m,
            MaintenanceMarginRateAfter = 0.004m,
            MaintenanceMarginAfter = 0.020856m,
            MarginBalanceAfter = 5.226m,
            LiquidationPriceAfter = 50m,
            OccurredAtUtc = now.AddMinutes(-5),
            CreatedDate = now.AddMinutes(-5),
            UpdatedDate = now.AddMinutes(-5)
        });
        await harness.DbContext.SaveChangesAsync();

        var snapshot = await harness.ExecutionGate.EnsureExecutionAllowedAsync(
            new ExecutionGateRequest(
                Actor: "worker-demo-recovery",
                Action: "TradeExecution.Dispatch",
                Target: "bot-demo-recovery",
                Environment: ExecutionEnvironment.Demo,
                Context: "Demo dispatch attempt after projection recovery",
                CorrelationId: "corr-demo-recovery-3",
                UserId: "user-demo-recovery",
                BotId: botId));

        var session = await harness.DbContext.DemoSessions.SingleAsync(entity => entity.OwnerUserId == "user-demo-recovery");
        var position = await harness.DbContext.DemoPositions.SingleAsync(entity => entity.BotId == botId);
        var recoveryAudit = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "DemoSession.MissingPositionProjectionRehydrated");
        var blockedCount = await harness.DbContext.AuditLogs.CountAsync(entity => entity.Outcome == "Blocked:DemoSessionDriftDetected");

        Assert.True(snapshot.IsPersisted);
        Assert.Equal(DemoConsistencyStatus.InSync, session.ConsistencyStatus);
        Assert.Null(session.LastDriftSummary);
        Assert.Equal(0.06m, position.Quantity);
        Assert.Contains("RehydratedPositions=1", recoveryAudit.Context, StringComparison.Ordinal);
        Assert.Equal(0, blockedCount);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenRequestedEnvironmentDoesNotMatchResolvedScopedMode()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-500");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-13",
            context: "Execution open",
            correlationId: "corr-501");
        await harness.SwitchService.SetDemoModeAsync(
            isEnabled: false,
            actor: "admin-13",
            liveApproval: new TradingModeLiveApproval("chg-502"),
            context: "Global default moved to live",
            correlationId: "corr-502");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-05",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-005",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Demo dispatch attempt against live scope",
                    CorrelationId: "corr-503")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.RequestedEnvironmentDoesNotMatchResolvedMode, exception.Reason);
        Assert.Equal("Blocked:RequestedEnvironmentDoesNotMatchResolvedMode", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_AllowsDevelopmentFuturesPilotOverride_WhenLivePathIsRequestedFromDemoMode()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            useTestnetEndpoints: true,
            pilotOptions: new BotExecutionPilotOptions
            {
                Enabled = true,
                PrivatePlaneFreshnessThresholdSeconds = 120
            });
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await PrimeFreshMarketDataAsync(harness, "corr-pilot-1");
        await SeedPilotSafetyAsync(harness, "user-pilot", exchangeAccountId);
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-pilot",
            context: "Execution open",
            correlationId: "corr-pilot-2");

        var snapshot = await harness.ExecutionGate.EnsureExecutionAllowedAsync(
            new ExecutionGateRequest(
                Actor: "system:bot-worker",
                Action: "TradeExecution.Dispatch",
                Target: "bot-pilot",
                Environment: ExecutionEnvironment.Live,
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                CorrelationId: "corr-pilot-3",
                UserId: "user-pilot",
                BotId: botId,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExchangeAccountId: exchangeAccountId,
                Plane: ExchangeDataPlane.Futures));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");
        var decisionTrace = await harness.DbContext.DecisionTraces.SingleAsync();

        Assert.True(snapshot.IsPersisted);
        Assert.Equal("Allowed", auditLog.Outcome);
        Assert.Contains("DecisionOutcome=Allow", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("PilotGuardSummary=PilotRequest=True", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("EndpointScopes=PrivateRest:Testnet/PrivateWs:Testnet/MarketRest:Testnet/MarketWs:Testnet", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("PilotBlockedReasons=none", auditLog.Context, StringComparison.Ordinal);
        Assert.Equal("Allow", decisionTrace.DecisionOutcome);
        Assert.Contains("\"pilotSafety\"", decisionTrace.SnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_AllowsDevelopmentFuturesPilotOverride_WhenExplicitBinanceTestnetPathIsRequested()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            useTestnetEndpoints: true,
            pilotOptions: new BotExecutionPilotOptions
            {
                Enabled = true,
                PrivatePlaneFreshnessThresholdSeconds = 120
            });
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await PrimeFreshMarketDataAsync(harness, "corr-pilot-testnet-1");
        await SeedPilotSafetyAsync(harness, "user-pilot-testnet", exchangeAccountId);
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-pilot-testnet",
            context: "Execution open",
            correlationId: "corr-pilot-testnet-2");

        var snapshot = await harness.ExecutionGate.EnsureExecutionAllowedAsync(
            new ExecutionGateRequest(
                Actor: "system:bot-worker",
                Action: "TradeExecution.Dispatch",
                Target: "bot-pilot-testnet",
                Environment: ExecutionEnvironment.BinanceTestnet,
                Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                CorrelationId: "corr-pilot-testnet-3",
                UserId: "user-pilot-testnet",
                BotId: botId,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExchangeAccountId: exchangeAccountId,
                Plane: ExchangeDataPlane.Futures));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.True(snapshot.IsPersisted);
        Assert.Equal("Allowed", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.BinanceTestnet), auditLog.Environment);
        Assert.Contains("PilotGuardSummary=PilotRequest=True", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("EndpointScopes=PrivateRest:Testnet/PrivateWs:Testnet/MarketRest:Testnet/MarketWs:Testnet", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("PilotBlockedReasons=none", auditLog.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksPilotRequest_WhenConfiguredEndpointsResolveLive()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            pilotOptions: new BotExecutionPilotOptions
            {
                Enabled = true,
                PrivatePlaneFreshnessThresholdSeconds = 120
            });
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await PrimeFreshMarketDataAsync(harness, "corr-pilot-live-1");
        await SeedPilotSafetyAsync(harness, "user-pilot-live", exchangeAccountId);
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-pilot-live",
            context: "Execution open",
            correlationId: "corr-pilot-live-2");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "system:bot-worker",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-pilot-live",
                    Environment: ExecutionEnvironment.Live,
                    Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                    CorrelationId: "corr-pilot-live-3",
                    UserId: "user-pilot-live",
                    BotId: botId,
                    Symbol: "BTCUSDT",
                    Timeframe: "1m",
                    ExchangeAccountId: exchangeAccountId,
                    Plane: ExchangeDataPlane.Futures)));

        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.PilotTestnetEndpointMismatch, exception.Reason);
        Assert.Contains("PilotBlockedReasons=PilotTestnetEndpointMismatch", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("EndpointScopes=PrivateRest:Live/PrivateWs:Live/MarketRest:Live/MarketWs:Live", auditLog.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksPilotRequest_WhenPrivatePlaneIsStale()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            useTestnetEndpoints: true,
            pilotOptions: new BotExecutionPilotOptions
            {
                Enabled = true,
                PrivatePlaneFreshnessThresholdSeconds = 120
            });
        var exchangeAccountId = Guid.NewGuid();
        var botId = Guid.NewGuid();
        await PrimeFreshMarketDataAsync(harness, "corr-pilot-stale-1");
        await SeedPilotSafetyAsync(
            harness,
            "user-pilot-stale",
            exchangeAccountId,
            lastPrivateSyncAtUtc: harness.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(-5));
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-pilot-stale",
            context: "Execution open",
            correlationId: "corr-pilot-stale-2");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "system:bot-worker",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-pilot-stale",
                    Environment: ExecutionEnvironment.Live,
                    Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                    CorrelationId: "corr-pilot-stale-3",
                    UserId: "user-pilot-stale",
                    BotId: botId,
                    Symbol: "BTCUSDT",
                    Timeframe: "1m",
                    ExchangeAccountId: exchangeAccountId,
                    Plane: ExchangeDataPlane.Futures)));

        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.PrivatePlaneStale, exception.Reason);
        Assert.Contains("PilotBlockedReasons=PrivatePlaneStale", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("PrivatePlaneFreshness=Stale", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("CredentialValidationStatus=Valid", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("PrivateStreamState=Connected", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DriftStatus=InSync", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("LastPrivateSyncAtUtc=", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("PrivatePlaneAgeMs=", auditLog.Context, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key", auditLog.Context, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api-secret", auditLog.Context, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksLiveExecutionWhenDemoModeIsEnabled_EvenIfUserOverrideResolvesLive()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-550");
        await SeedUserAsync(
            harness.DbContext,
            "user-live-override",
            ExecutionEnvironment.Live,
            harness.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(-5));
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-override",
            context: "Execution open",
            correlationId: "corr-551");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-override",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-override",
                    Environment: ExecutionEnvironment.Live,
                    Context: "Live dispatch attempt against user override",
                    CorrelationId: "corr-552",
                    UserId: "user-live-override")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.LiveExecutionBlockedByDemoMode, exception.Reason);
        Assert.Equal("Blocked:LiveExecutionClosedByDemoMode", auditLog.Outcome);
        Assert.Contains("ResolvedMode=Live; Source=UserOverride", auditLog.Context, StringComparison.Ordinal);
    }


    [Fact]
    public async Task EnsureExecutionAllowedAsync_AppendsBreakerDiagnostics_WhenGlobalSystemStateIsDegraded()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-gs-degraded-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-gs-degraded",
            context: "Execution open",
            correlationId: "corr-gs-degraded-2");

        harness.DbContext.DependencyCircuitBreakerStates.Add(
            new DependencyCircuitBreakerState
            {
                Id = Guid.NewGuid(),
                BreakerKind = DependencyCircuitBreakerKind.OrderExecution,
                StateCode = CircuitBreakerStateCode.Cooldown,
                ConsecutiveFailureCount = 4,
                CooldownUntilUtc = DateTime.UtcNow.AddMinutes(5),
                LastFailureAtUtc = DateTime.UtcNow.AddMinutes(-1),
                LastErrorCode = "ClockDriftExceeded",
                LastErrorMessage = "Execution blocked because clock drift exceeded the safety threshold.",
                CorrelationId = "corr-breaker-diag"
            });
        await harness.DbContext.SaveChangesAsync();

        await harness.GlobalSystemStateService.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.Degraded,
                "AUTONOMY_BREAKER_ORDEREXECUTION",
                "Dependency breaker OrderExecution is in cooldown.",
                "Autonomy.DependencyBreaker",
                "corr-gs-degraded-3",
                IsManualOverride: false,
                ExpiresAtUtc: DateTime.UtcNow.AddMinutes(5),
                UpdatedByUserId: "system:autonomy-self-healing",
                UpdatedFromIp: null));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-gs-degraded",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-gs-degraded",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Dispatch during degraded breaker cooldown",
                    CorrelationId: "corr-gs-degraded-4")));

        Assert.Contains("ActiveBreakerDiagnostics=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("BreakerKind=OrderExecution", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ErrorCode=ClockDriftExceeded", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CorrelationId=corr-breaker-diag", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenGlobalSystemStateIsMaintenance()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-gs-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-gs",
            context: "Execution open",
            correlationId: "corr-gs-2");
        await harness.GlobalSystemStateService.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.Maintenance,
                "PLANNED_MAINTENANCE",
                "Controlled window",
                "AdminPortal.Settings",
                "corr-gs-3",
                IsManualOverride: true,
                ExpiresAtUtc: null,
                UpdatedByUserId: "super-admin",
                UpdatedFromIp: "ip:masked"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-gs",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-gs",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Dispatch during maintenance",
                    CorrelationId: "corr-gs-4")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");
        var decisionTrace = await harness.DbContext.DecisionTraces.SingleAsync();

        Assert.Contains("Maintenance", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Blocked:GlobalSystemMaintenance", auditLog.Outcome);
        Assert.Contains("DecisionOutcome=Block", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonType=GlobalExecutionOff", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonCode=GlobalSystemMaintenance", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("GlobalSystemReason=PLANNED_MAINTENANCE", auditLog.Context, StringComparison.Ordinal);
        Assert.Equal("Block", decisionTrace.DecisionOutcome);
        Assert.Equal("GlobalExecutionOff", decisionTrace.DecisionReasonType);
        Assert.Equal("GlobalSystemMaintenance", decisionTrace.DecisionReasonCode);
        Assert.NotNull(decisionTrace.DecisionAtUtc);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenGlobalSystemStateIsSoftHalt()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-gs-soft-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-gs-soft",
            context: "Execution open",
            correlationId: "corr-gs-soft-2");
        await harness.GlobalSystemStateService.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.SoftHalt,
                "SOFT_HALT_ACTIVE",
                "Stabilize execution entry",
                "AdminPortal.Settings",
                "corr-gs-soft-3",
                IsManualOverride: true,
                ExpiresAtUtc: null,
                UpdatedByUserId: "super-admin",
                UpdatedFromIp: "ip:masked"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-gs-soft",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-gs-soft",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Dispatch during soft halt",
                    CorrelationId: "corr-gs-soft-4")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");
        var decisionTrace = await harness.DbContext.DecisionTraces.SingleAsync();

        Assert.Contains("SoftHalt", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Blocked:GlobalSystemSoftHalt", auditLog.Outcome);
        Assert.Contains("DecisionOutcome=Block", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonType=GlobalExecutionOff", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonCode=GlobalSystemSoftHalt", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("GlobalSystemReason=SOFT_HALT_ACTIVE", auditLog.Context, StringComparison.Ordinal);
        Assert.Equal("Block", decisionTrace.DecisionOutcome);
        Assert.Equal("GlobalExecutionOff", decisionTrace.DecisionReasonType);
        Assert.Equal("GlobalSystemSoftHalt", decisionTrace.DecisionReasonCode);
        Assert.Equal("Execution blocked because global system state is SoftHalt: Stabilize execution entry", decisionTrace.DecisionSummary);
        Assert.NotNull(decisionTrace.DecisionAtUtc);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenGlobalSystemStateIsFullHalt()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-gs-full-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-gs-full",
            context: "Execution open",
            correlationId: "corr-gs-full-2");
        await harness.GlobalSystemStateService.SetStateAsync(
            new GlobalSystemStateSetRequest(
                GlobalSystemStateKind.FullHalt,
                "EMERGENCY_STOP_ACTIVE",
                "Emergency stop active",
                "AdminPortal.Settings",
                "corr-gs-full-3",
                IsManualOverride: true,
                ExpiresAtUtc: null,
                UpdatedByUserId: "super-admin",
                UpdatedFromIp: "ip:masked"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-gs-full",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-gs-full",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Dispatch during full halt",
                    CorrelationId: "corr-gs-full-4")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");
        var decisionTrace = await harness.DbContext.DecisionTraces.SingleAsync();

        Assert.Contains("FullHalt", exception.Message, StringComparison.Ordinal);
        Assert.Equal("Blocked:GlobalSystemFullHalt", auditLog.Outcome);
        Assert.Contains("DecisionOutcome=Block", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonType=GlobalExecutionOff", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonCode=GlobalSystemFullHalt", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("GlobalSystemReason=EMERGENCY_STOP_ACTIVE", auditLog.Context, StringComparison.Ordinal);
        Assert.Equal("Block", decisionTrace.DecisionOutcome);
        Assert.Equal("GlobalExecutionOff", decisionTrace.DecisionReasonType);
        Assert.Equal("GlobalSystemFullHalt", decisionTrace.DecisionReasonCode);
        Assert.Equal("Execution blocked because global system state is FullHalt: Emergency stop active", decisionTrace.DecisionSummary);
        Assert.NotNull(decisionTrace.DecisionAtUtc);
    }
    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenMarketDataIsStale()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-600");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-14",
            context: "Execution open",
            correlationId: "corr-601");
        harness.TimeProvider.Advance(TimeSpan.FromSeconds(3));

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-06",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-006",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Dispatch against stale market data",
                    CorrelationId: "corr-602")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");
        var decisionTrace = await harness.DbContext.DecisionTraces.SingleAsync();

        Assert.Equal(ExecutionGateBlockedReason.StaleMarketData, exception.Reason);
        Assert.Equal("Blocked:StaleMarketData", auditLog.Outcome);
        Assert.Equal(nameof(ExecutionEnvironment.Demo), auditLog.Environment);
        Assert.Contains("Execution blocked because market data is stale.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("LatencyReason=MarketDataLatencyBreached", exception.Message, StringComparison.Ordinal);
        Assert.Contains("HeartbeatSource=binance:kline", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Symbol=BTCUSDT", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Timeframe=1m", exception.Message, StringComparison.Ordinal);
        Assert.Contains("LastCandleAtUtc=2026-03-22T12:00:00.0000000Z", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DataAgeMs=3000", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ContinuityGapCount=0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DecisionSourceLayer=heartbeat-watchdog", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DecisionMethodName=ExecutionGate.EvaluateDataLatencyAsync", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Symbol=BTCUSDT", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("Timeframe=1m", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionOutcome=Block", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonType=StaleData", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonCode=StaleMarketData", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("ContinuityGapCount=0", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("ContinuityGapStartedAtUtc=missing", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("ContinuityGapLastSeenAtUtc=missing", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("ContinuityRecoveredAtUtc=missing", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("StaleThresholdMs=3000", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("StaleReason=Market data stale", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionSourceLayer=heartbeat-watchdog", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionMethodName=ExecutionGate.EvaluateDataLatencyAsync", auditLog.Context, StringComparison.Ordinal);
        Assert.Equal("Block", decisionTrace.DecisionOutcome);
        Assert.Equal("StaleData", decisionTrace.DecisionReasonType);
        Assert.Equal("StaleMarketData", decisionTrace.DecisionReasonCode);
        Assert.Equal("Execution blocked because market data is stale.", decisionTrace.DecisionSummary);
        Assert.Equal(new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc), decisionTrace.LastCandleAtUtc);
        Assert.Equal(3000, decisionTrace.DataAgeMs);
        Assert.Equal(3000, decisionTrace.StaleThresholdMs);
        Assert.Equal("Market data stale", decisionTrace.StaleReason);
        Assert.Equal("Continuity OK", decisionTrace.ContinuityState);
        Assert.NotNull(decisionTrace.DecisionAtUtc);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksWhenCandleGapDetected()
    {
        await using var harness = CreateHarness();
        await PrimeFreshMarketDataAsync(harness, "corr-700");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-15",
            context: "Execution open",
            correlationId: "corr-701");
        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.CandleDataGapDetected,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc),
                ContinuityGapCount: 3),
            correlationId: "corr-702");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "worker-07",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-007",
                    Environment: ExecutionEnvironment.Demo,
                    Context: "Dispatch against candle gap",
                    CorrelationId: "corr-703")));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");
        var decisionTrace = await harness.DbContext.DecisionTraces.SingleAsync();

        Assert.Equal(ExecutionGateBlockedReason.ContinuityGap, exception.Reason);
        Assert.Equal("Blocked:ContinuityGap", auditLog.Outcome);
        Assert.Contains("Execution blocked because the candle continuity guard is active.", exception.Message, StringComparison.Ordinal);
        Assert.Contains("LatencyReason=CandleDataGapDetected", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Symbol=BTCUSDT", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Timeframe=1m", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ExpectedOpenTimeUtc=2026-03-22T12:01:00.0000000Z", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ContinuityGapCount=3", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DecisionSourceLayer=continuity-validator", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DecisionMethodName=ExecutionGate.EvaluateDataLatencyAsync", exception.Message, StringComparison.Ordinal);
        Assert.Contains("LatencyReason=CandleDataGapDetected", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("Symbol=BTCUSDT", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("Timeframe=1m", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionOutcome=Block", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonType=ContinuityGap", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionReasonCode=ContinuityGap", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("ExpectedOpenTimeUtc=2026-03-22T12:01:00.0000000Z", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("ContinuityGapCount=3", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("ContinuityGapStartedAtUtc=", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("ContinuityGapLastSeenAtUtc=", auditLog.Context, StringComparison.Ordinal);
        Assert.DoesNotContain("ContinuityGapStartedAtUtc=missing", auditLog.Context, StringComparison.Ordinal);
        Assert.DoesNotContain("ContinuityGapLastSeenAtUtc=missing", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("ContinuityRecoveredAtUtc=missing", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionSourceLayer=continuity-validator", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("DecisionMethodName=ExecutionGate.EvaluateDataLatencyAsync", auditLog.Context, StringComparison.Ordinal);
        Assert.Equal("Block", decisionTrace.DecisionOutcome);
        Assert.Equal("ContinuityGap", decisionTrace.DecisionReasonType);
        Assert.Equal("ContinuityGap", decisionTrace.DecisionReasonCode);
        Assert.Equal("Execution blocked because the candle continuity guard is active.", decisionTrace.DecisionSummary);
        Assert.Equal(3, decisionTrace.ContinuityGapCount);
        Assert.Equal("Continuity guard active", decisionTrace.ContinuityState);
        Assert.Equal("Continuity gap detected", decisionTrace.StaleReason);
        Assert.Equal(new DateTime(2026, 3, 22, 12, 1, 0, DateTimeKind.Utc), decisionTrace.ContinuityGapStartedAtUtc);
        Assert.Equal(new DateTime(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc), decisionTrace.ContinuityGapLastSeenAtUtc);
        Assert.Null(decisionTrace.ContinuityRecoveredAtUtc);
        Assert.NotNull(decisionTrace.DecisionAtUtc);
    }

    [Fact]
    public async Task EnsureExecutionAllowedAsync_UsesSymbolScopedLatencySnapshot_WhenGlobalSingletonWasUpdatedByAnotherSymbol()
    {
        await using var harness = CreateHarness();
        var nowUtc = harness.TimeProvider.GetUtcNow().UtcDateTime;

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                nowUtc,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: nowUtc.AddMinutes(1),
                ContinuityGapCount: 0),
            correlationId: "corr-cross-btc");

        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                nowUtc.AddSeconds(-10),
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.MarketDataLatencyCritical,
                Symbol: "ETHUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: nowUtc,
                ContinuityGapCount: 0),
            correlationId: "corr-cross-eth");

        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-cross",
            context: "Execution open",
            correlationId: "corr-cross-switch");

        var snapshot = await harness.ExecutionGate.EnsureExecutionAllowedAsync(
            new ExecutionGateRequest(
                Actor: "worker-cross",
                Action: "TradeExecution.Dispatch",
                Target: "bot-cross-btc",
                Environment: ExecutionEnvironment.Demo,
                Context: "BTC dispatch against fresh scoped state",
                CorrelationId: "corr-cross-order",
                Symbol: "BTCUSDT",
                Timeframe: "1m"));

        var auditLog = await harness.DbContext.AuditLogs
            .SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");
        var btcStateId = DegradedModeDefaults.ResolveStateId("BTCUSDT", "1m");
        var btcState = await harness.DbContext.DegradedModeStates.SingleAsync(entity => entity.Id == btcStateId);
        var singletonState = await harness.DbContext.DegradedModeStates.SingleAsync(entity => entity.Id == DegradedModeDefaults.SingletonId);

        Assert.True(snapshot.IsPersisted);
        Assert.True(snapshot.IsTradeMasterArmed);
        Assert.Equal("Allowed", auditLog.Outcome);
        Assert.Contains("LatencyReason=None", auditLog.Context, StringComparison.Ordinal);
        Assert.Contains("Symbol=BTCUSDT", auditLog.Context, StringComparison.Ordinal);
        Assert.DoesNotContain("Symbol=ETHUSDT", auditLog.Context, StringComparison.Ordinal);
        Assert.Equal(DegradedModeStateCode.Normal, btcState.StateCode);
        Assert.Equal(DegradedModeReasonCode.None, btcState.ReasonCode);
        Assert.Equal("BTCUSDT", btcState.LatestSymbol);
        Assert.Equal(DegradedModeStateCode.Stopped, singletonState.StateCode);
        Assert.Equal(DegradedModeReasonCode.MarketDataLatencyCritical, singletonState.ReasonCode);
        Assert.Equal("ETHUSDT", singletonState.LatestSymbol);
    }
    [Fact]
    public async Task EnsureExecutionAllowedAsync_BlocksPilotRequest_WhenConfigurationIsIncomplete()
    {
        await using var harness = CreateHarness(
            environmentName: Environments.Development,
            useTestnetEndpoints: true,
            pilotOptions: new BotExecutionPilotOptions
            {
                Enabled = true,
                PrivatePlaneFreshnessThresholdSeconds = 120
            });
        var botId = Guid.NewGuid();
        await PrimeFreshMarketDataAsync(harness, "corr-pilot-config-1");
        await harness.SwitchService.SetTradeMasterStateAsync(
            TradeMasterSwitchState.Armed,
            actor: "admin-pilot-config",
            context: "Execution open",
            correlationId: "corr-pilot-config-2");

        var exception = await Assert.ThrowsAsync<ExecutionGateRejectedException>(() =>
            harness.ExecutionGate.EnsureExecutionAllowedAsync(
                new ExecutionGateRequest(
                    Actor: "system:bot-worker",
                    Action: "TradeExecution.Dispatch",
                    Target: "bot-pilot-config",
                    Environment: ExecutionEnvironment.Live,
                    Context: "DevelopmentFuturesTestnetPilot=True | PilotMarginType=ISOLATED | PilotLeverage=1",
                    CorrelationId: "corr-pilot-config-3",
                    UserId: "user-pilot-config",
                    BotId: botId,
                    Symbol: "BTCUSDT",
                    Timeframe: "1m",
                    Plane: ExchangeDataPlane.Futures)));

        var auditLog = await harness.DbContext.AuditLogs.SingleAsync(entity => entity.Action == "TradeExecution.Dispatch");

        Assert.Equal(ExecutionGateBlockedReason.PilotConfigurationMissing, exception.Reason);
        Assert.Contains("PilotBlockedReasons=PilotConfigurationMissing", auditLog.Context, StringComparison.Ordinal);
    }

    private static TestHarness CreateHarness(
        string environmentName = "Production",
        bool useTestnetEndpoints = false,
        BotExecutionPilotOptions? pilotOptions = null)
    {
        var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 3, 22, 12, 0, 0, TimeSpan.Zero));
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        pilotOptions ??= new BotExecutionPilotOptions();
        var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
        var auditLogService = new AuditLogService(dbContext, new CorrelationContextAccessor());
        var switchService = new GlobalExecutionSwitchService(dbContext, auditLogService);
        var globalSystemStateService = new GlobalSystemStateService(dbContext, auditLogService, timeProvider);
        var traceService = new TraceService(
            dbContext,
            new CorrelationContextAccessor(),
            timeProvider);
        var latencyOptions = Options.Create(new DataLatencyGuardOptions());
        var circuitBreaker = new DataLatencyCircuitBreaker(
            dbContext,
            new FakeAlertService(),
            latencyOptions,
            timeProvider,
            NullLogger<DataLatencyCircuitBreaker>.Instance);
        var tradingModeService = new TradingModeService(dbContext, auditLogService);
        var marketDataService = new FakeMarketDataService();
        var demoWalletValuationService = new DemoWalletValuationService(
            marketDataService,
            timeProvider,
            NullLogger<DemoWalletValuationService>.Instance);
        var demoSessionService = new DemoSessionService(
            dbContext,
            new DemoConsistencyWatchdogService(
                dbContext,
                Options.Create(new DemoSessionOptions()),
                timeProvider,
                NullLogger<DemoConsistencyWatchdogService>.Instance),
            demoWalletValuationService,
            auditLogService,
            Options.Create(new DemoSessionOptions()),
            timeProvider,
            NullLogger<DemoSessionService>.Instance);
        var privateDataOptions = Options.Create(new BinancePrivateDataOptions
        {
            RestBaseUrl = useTestnetEndpoints
                ? "https://testnet.binancefuture.com"
                : "https://fapi.binance.com",
            WebSocketBaseUrl = useTestnetEndpoints
                ? "wss://fstream.binancefuture.com"
                : "wss://fstream.binance.com"
        });
        var marketDataOptions = Options.Create(new BinanceMarketDataOptions
        {
            RestBaseUrl = useTestnetEndpoints
                ? "https://testnet.binancefuture.com"
                : "https://fapi.binance.com",
            WebSocketBaseUrl = useTestnetEndpoints
                ? "wss://fstream.binancefuture.com"
                : "wss://fstream.binance.com",
            KlineInterval = "1m"
        });
        var executionGate = new ExecutionGate(
            demoSessionService,
            globalSystemStateService,
            switchService,
            circuitBreaker,
            tradingModeService,
            auditLogService,
            NullLogger<ExecutionGate>.Instance,
            new TestHostEnvironment(environmentName),
            traceService,
            timeProvider,
            latencyOptions,
            dbContext,
            privateDataOptions,
            marketDataOptions,
            Options.Create(pilotOptions));

        return new TestHarness(dbContext, switchService, globalSystemStateService, circuitBreaker, executionGate, timeProvider, pilotOptions);
    }

    private static async Task PrimeFreshMarketDataAsync(TestHarness harness, string correlationId)
    {
        await harness.CircuitBreaker.RecordHeartbeatAsync(
            new DataLatencyHeartbeat(
                "binance:kline",
                harness.TimeProvider.GetUtcNow().UtcDateTime,
                Symbol: "BTCUSDT",
                Timeframe: "1m",
                ExpectedOpenTimeUtc: harness.TimeProvider.GetUtcNow().UtcDateTime.AddMinutes(1),
                ContinuityGapCount: 0),
            correlationId);
    }

    private static async Task SeedPilotSafetyAsync(
        TestHarness harness,
        string ownerUserId,
        Guid exchangeAccountId,
        DateTime? lastPrivateSyncAtUtc = null,
        string environmentScope = "Testnet",
        bool isEnvironmentMatch = true,
        bool canTrade = true,
        bool supportsFutures = true,
        ExchangePrivateStreamConnectionState connectionState = ExchangePrivateStreamConnectionState.Connected,
        ExchangeStateDriftStatus driftStatus = ExchangeStateDriftStatus.InSync)
    {
        var observedAtUtc = lastPrivateSyncAtUtc ?? harness.TimeProvider.GetUtcNow().UtcDateTime;
        harness.DbContext.ApiCredentialValidations.Add(new ApiCredentialValidation
        {
            Id = Guid.NewGuid(),
            ApiCredentialId = Guid.NewGuid(),
            ExchangeAccountId = exchangeAccountId,
            OwnerUserId = ownerUserId,
            IsKeyValid = true,
            CanTrade = canTrade,
            SupportsSpot = false,
            SupportsFutures = supportsFutures,
            EnvironmentScope = environmentScope,
            IsEnvironmentMatch = isEnvironmentMatch,
            ValidationStatus = canTrade && supportsFutures ? "Valid" : "Invalid",
            PermissionSummary = "Trade=Y; Futures=Y; Testnet=Y",
            ValidatedAtUtc = observedAtUtc
        });
        harness.DbContext.ExchangeAccountSyncStates.Add(new ExchangeAccountSyncState
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            ExchangeAccountId = exchangeAccountId,
            Plane = ExchangeDataPlane.Futures,
            PrivateStreamConnectionState = connectionState,
            DriftStatus = driftStatus,
            LastPrivateStreamEventAtUtc = observedAtUtc,
            LastBalanceSyncedAtUtc = observedAtUtc,
            LastPositionSyncedAtUtc = observedAtUtc,
            LastStateReconciledAtUtc = observedAtUtc
        });

        await harness.DbContext.SaveChangesAsync();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "CoinBot.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task SeedUserAsync(
        ApplicationDbContext dbContext,
        string userId,
        ExecutionEnvironment? modeOverride,
        DateTime? approvedAtUtc = null)
    {
        dbContext.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = userId,
            NormalizedUserName = userId.ToUpperInvariant(),
            Email = $"{userId}@example.test",
            NormalizedEmail = $"{userId}@example.test".ToUpperInvariant(),
            FullName = userId,
            TradingModeOverride = modeOverride,
            TradingModeApprovedAtUtc = approvedAtUtc,
            TradingModeApprovalReference = approvedAtUtc.HasValue ? "approval-user-override" : null
        });

        await dbContext.SaveChangesAsync();
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }

    private sealed class FakeAlertService : IAlertService
    {
        public Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMarketDataService : IMarketDataService
    {
        public ValueTask TrackSymbolAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask TrackSymbolsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<MarketPriceSnapshot?> GetLatestPriceAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MarketPriceSnapshot?>(null);
        }

        public ValueTask<SymbolMetadataSnapshot?> GetSymbolMetadataAsync(string symbol, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SymbolMetadataSnapshot?>(null);
        }

        public async IAsyncEnumerable<MarketPriceSnapshot> WatchAsync(
            IEnumerable<string> symbols,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestHarness(
        ApplicationDbContext dbContext,
        IGlobalExecutionSwitchService switchService,
        IGlobalSystemStateService globalSystemStateService,
        IDataLatencyCircuitBreaker circuitBreaker,
        IExecutionGate executionGate,
        AdjustableTimeProvider timeProvider,
        BotExecutionPilotOptions pilotOptions) : IAsyncDisposable
    {
        public ApplicationDbContext DbContext { get; } = dbContext;

        public IGlobalExecutionSwitchService SwitchService { get; } = switchService;

        public IGlobalSystemStateService GlobalSystemStateService { get; } = globalSystemStateService;

        public IDataLatencyCircuitBreaker CircuitBreaker { get; } = circuitBreaker;

        public IExecutionGate ExecutionGate { get; } = executionGate;

        public AdjustableTimeProvider TimeProvider { get; } = timeProvider;

        public BotExecutionPilotOptions PilotOptions { get; } = pilotOptions;

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }
}
