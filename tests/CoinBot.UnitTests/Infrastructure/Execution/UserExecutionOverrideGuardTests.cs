using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class UserExecutionOverrideGuardTests
{
    [Fact]
    public async Task EvaluateAsync_BlocksWhenGlobalPolicyBlocks_RegardlessOfUserOverride()
    {
        await using var dbContext = CreateDbContext();
        dbContext.UserExecutionOverrides.Add(new UserExecutionOverride
        {
            UserId = "user-01",
            AllowedSymbolsCsv = "BTCUSDT",
            DeniedSymbolsCsv = string.Empty,
            MaxOrderSize = 1_000_000m,
            MaxDailyTrades = 20,
            ReduceOnly = false,
            SessionDisabled = false
        });
        await dbContext.SaveChangesAsync();

        var guard = new UserExecutionOverrideGuard(
            dbContext,
            new FakeTradingModeResolver(),
            new FakeGlobalPolicyEngine(
                new GlobalPolicyEvaluationResult(
                    true,
                    "GlobalPolicyObserveOnly",
                    "Global policy is in ObserveOnly mode and execution is blocked.",
                    9,
                    null,
                    AutonomyPolicyMode.ObserveOnly)),
            NullLogger<UserExecutionOverrideGuard>.Instance);

        var result = await guard.EvaluateAsync(
            new UserExecutionOverrideEvaluationRequest(
                "user-01",
                "BTCUSDT",
                ExecutionEnvironment.Demo,
                ExecutionOrderSide.Buy,
                1m,
                100m,
                BotId: null,
                StrategyKey: "core"),
            CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("GlobalPolicyObserveOnly", result.BlockCode);
        Assert.Contains("ObserveOnly", result.Message, StringComparison.Ordinal);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class FakeTradingModeResolver : ITradingModeResolver
    {
        public Task<TradingModeResolution> ResolveAsync(TradingModeResolutionRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TradingModeResolution(
                ExecutionEnvironment.Demo,
                null,
                null,
                null,
                ExecutionEnvironment.Demo,
                TradingModeResolutionSource.GlobalDefault,
                "Demo",
                false));
        }
    }

    private sealed class FakeGlobalPolicyEngine : IGlobalPolicyEngine
    {
        private readonly GlobalPolicyEvaluationResult evaluationResult;

        public FakeGlobalPolicyEngine(GlobalPolicyEvaluationResult evaluationResult)
        {
            this.evaluationResult = evaluationResult;
        }

        public Task<GlobalPolicySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(GlobalPolicySnapshot.CreateDefault(DateTime.UtcNow));
        }

        public Task<GlobalPolicyEvaluationResult> EvaluateAsync(GlobalPolicyEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(evaluationResult);
        }

        public Task<GlobalPolicySnapshot> UpdateAsync(GlobalPolicyUpdateRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<GlobalPolicySnapshot> RollbackAsync(GlobalPolicyRollbackRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestDataScopeContext : CoinBot.Application.Abstractions.DataScope.IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
