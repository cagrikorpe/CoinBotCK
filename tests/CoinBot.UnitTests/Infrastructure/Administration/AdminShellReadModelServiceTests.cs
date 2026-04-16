using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Infrastructure.Administration;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class AdminShellReadModelServiceTests
{
    [Fact]
    public async Task GetHealthSnapshotAsync_UsesSizedCacheEntry_WhenMemoryCacheHasSizeLimit()
    {
        await using var dbContext = CreateDbContext();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 128 });
        var service = new AdminShellReadModelService(dbContext, memoryCache);

        var snapshot = await service.GetHealthSnapshotAsync();
        var cachedSnapshot = await service.GetHealthSnapshotAsync();

        Assert.Equal(snapshot.EnvironmentBadge, cachedSnapshot.EnvironmentBadge);
        Assert.Equal(snapshot.SystemState, cachedSnapshot.SystemState);
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
}
