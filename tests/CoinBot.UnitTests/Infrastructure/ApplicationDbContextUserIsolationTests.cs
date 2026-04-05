using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Security.Claims;

namespace CoinBot.UnitTests.Infrastructure;

public sealed class ApplicationDbContextUserIsolationTests
{
    [Fact]
    public async Task SaveChanges_AssignsCurrentUserAsOwner_ForNewRiskProfile()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var harness = CreateHarness(databaseRoot, userId: "user-risk");

        var riskProfile = new RiskProfile
        {
            ProfileName = "Balanced",
            MaxDailyLossPercentage = 2.5m,
            MaxPositionSizePercentage = 10m,
            KillSwitchEnabled = true
        };

        harness.Context.RiskProfiles.Add(riskProfile);
        await harness.Context.SaveChangesAsync();

        Assert.Equal("user-risk", riskProfile.OwnerUserId);
    }

    [Fact]
    public async Task QueryFilter_ReturnsOnlyCurrentUsersExchangeAccounts()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();

        await SeedExchangeAccountsAsync(databaseName, databaseRoot);

        await using var userOneHarness = CreateHarness(databaseRoot, userId: "user-one", databaseName: databaseName);
        await using var userTwoHarness = CreateHarness(databaseRoot, userId: "user-two", databaseName: databaseName);

        var userOneAccounts = await userOneHarness.Context.ExchangeAccounts
            .OrderBy(entity => entity.DisplayName)
            .Select(entity => entity.DisplayName)
            .ToListAsync();

        var userTwoAccounts = await userTwoHarness.Context.ExchangeAccounts
            .OrderBy(entity => entity.DisplayName)
            .Select(entity => entity.DisplayName)
            .ToListAsync();

        Assert.Equal(["User One Primary"], userOneAccounts);
        Assert.Equal(["User Two Primary"], userTwoAccounts);
    }

    [Fact]
    public async Task SaveChanges_RejectsCrossUserTradingBotWrite_WhenBypassIsOff()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var harness = CreateHarness(databaseRoot, userId: "user-one");

        harness.Context.TradingBots.Add(new TradingBot
        {
            OwnerUserId = "user-two",
            Name = "Momentum",
            StrategyKey = "momentum-core",
            IsEnabled = true
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Context.SaveChangesAsync());

        Assert.Contains("User scope mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedBypass_AllowsSystemReadAcrossUsers_AndExplicitOwnerWrite()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();

        await SeedExchangeAccountsAsync(databaseName, databaseRoot);

        await using var systemHarness = CreateHarness(databaseRoot, databaseName: databaseName, hasIsolationBypass: true);

        systemHarness.Context.ExchangeAccounts.Add(new ExchangeAccount
        {
            OwnerUserId = "user-three",
            ExchangeName = "Kraken",
            DisplayName = "User Three Primary",
            IsReadOnly = true
        });

        await systemHarness.Context.SaveChangesAsync();

        await using var bypassHarness = CreateHarness(databaseRoot, databaseName: databaseName, hasIsolationBypass: true);

        var allAccountNames = await bypassHarness.Context.ExchangeAccounts
            .OrderBy(entity => entity.DisplayName)
            .Select(entity => entity.DisplayName)
            .ToListAsync();

        Assert.Equal(
            ["User One Primary", "User Three Primary", "User Two Primary"],
            allAccountNames);
    }

    [Fact]
    public async Task EnsureCurrentUserScope_RejectsCrossUserScope_WhenIsolationBypassIsOff()
    {
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var harness = CreateHarness(databaseRoot, userId: "scope-user-a");

        var exception = Assert.Throws<InvalidOperationException>(() => harness.Context.EnsureCurrentUserScope("scope-user-b"));

        Assert.Contains("outside the authenticated isolation boundary", exception.Message, StringComparison.Ordinal);
    }

    private static async Task SeedExchangeAccountsAsync(string databaseName, InMemoryDatabaseRoot databaseRoot)
    {
        await using var harness = CreateHarness(databaseRoot, databaseName: databaseName, hasIsolationBypass: true);

        harness.Context.ExchangeAccounts.AddRange(
            new ExchangeAccount
            {
                OwnerUserId = "user-one",
                ExchangeName = "Binance",
                DisplayName = "User One Primary",
                IsReadOnly = false
            },
            new ExchangeAccount
            {
                OwnerUserId = "user-two",
                ExchangeName = "Bybit",
                DisplayName = "User Two Primary",
                IsReadOnly = false
            });

        await harness.Context.SaveChangesAsync();
    }

    private static TestHarness CreateHarness(
        InMemoryDatabaseRoot databaseRoot,
        string? userId = null,
        string? databaseName = null,
        bool hasIsolationBypass = false)
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = CreateHttpContext(userId)
        };

        var scopeAccessor = new DataScopeContextAccessor(httpContextAccessor);

        IDisposable? scopeLease = null;

        if (hasIsolationBypass)
        {
            scopeLease = scopeAccessor.BeginScope(hasIsolationBypass: true);
        }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"), databaseRoot)
            .Options;

        return new TestHarness(new ApplicationDbContext(options, scopeAccessor), scopeAccessor, scopeLease);
    }

    private static DefaultHttpContext CreateHttpContext(string? userId)
    {
        var httpContext = new DefaultHttpContext();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return httpContext;
        }

        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId)],
                authenticationType: "TestAuthentication"));

        return httpContext;
    }

    private sealed class TestHarness(
        ApplicationDbContext context,
        IDataScopeContextAccessor scopeAccessor,
        IDisposable? scopeLease) : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; } = context;

        public IDataScopeContextAccessor ScopeAccessor { get; } = scopeAccessor;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            scopeLease?.Dispose();
        }
    }
}
