using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Settings;

public sealed class UserSettingsServiceTests
{
    [Fact]
    public async Task GetAsync_LoadsPersistedTimeZone_AndReturnsClockSnapshot()
    {
        var timeSyncSnapshot = new BinanceTimeSyncSnapshot(
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 2, 10, 0, 1, 500, DateTimeKind.Utc),
            1500,
            24,
            new DateTime(2026, 4, 2, 10, 0, 1, DateTimeKind.Utc),
            "Synchronized",
            null);
        await using var harness = await TestHarness.CreateAsync(timeSyncSnapshot);
        var timeZoneId = ResolveNonUtcTimeZoneId();
        harness.User.PreferredTimeZoneId = timeZoneId;
        await harness.DbContext.SaveChangesAsync();

        var snapshot = await harness.Service.GetAsync(harness.User.Id);

        Assert.NotNull(snapshot);
        Assert.Equal(timeZoneId, snapshot!.PreferredTimeZoneId);
        Assert.Equal(timeSyncSnapshot.OffsetMilliseconds, snapshot.BinanceTimeSync.OffsetMilliseconds);
        Assert.Contains(snapshot.TimeZoneOptions, option => option.TimeZoneId == timeZoneId);
    }

    [Fact]
    public async Task SaveAsync_PersistsPreferredTimeZoneId()
    {
        await using var harness = await TestHarness.CreateAsync();
        var timeZoneId = ResolveNonUtcTimeZoneId();

        var result = await harness.Service.SaveAsync(
            harness.User.Id,
            new CoinBot.Application.Abstractions.Settings.UserSettingsSaveCommand(timeZoneId),
            "user:test");

        var reloadedUser = await harness.DbContext.Users.SingleAsync(entity => entity.Id == harness.User.Id);

        Assert.True(result.IsSuccessful);
        Assert.Equal(timeZoneId, result.PreferredTimeZoneId);
        Assert.Equal(timeZoneId, reloadedUser.PreferredTimeZoneId);
    }

    [Fact]
    public async Task SaveAsync_ReturnsFailure_WhenTimeZoneIsInvalid()
    {
        await using var harness = await TestHarness.CreateAsync();

        var result = await harness.Service.SaveAsync(
            harness.User.Id,
            new CoinBot.Application.Abstractions.Settings.UserSettingsSaveCommand("Mars/Phobos"),
            "user:test");

        Assert.False(result.IsSuccessful);
        Assert.Equal("TimeZoneInvalid", result.FailureCode);
    }

    private static string ResolveNonUtcTimeZoneId()
    {
        return TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => zone.Id)
            .FirstOrDefault(id => !string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase))
            ?? "UTC";
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly ApplicationDbContext dbContext;

        private TestHarness(ApplicationDbContext dbContext, ApplicationUser user, UserSettingsService service)
        {
            this.dbContext = dbContext;
            User = user;
            Service = service;
        }

        public ApplicationDbContext DbContext => dbContext;

        public ApplicationUser User { get; }

        public UserSettingsService Service { get; }

        public static async Task<TestHarness> CreateAsync(BinanceTimeSyncSnapshot? snapshot = null)
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            var dbContext = new ApplicationDbContext(options, new TestDataScopeContext());
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid().ToString("N"),
                UserName = "settings.user@coinbot.test",
                NormalizedUserName = "SETTINGS.USER@COINBOT.TEST",
                Email = "settings.user@coinbot.test",
                NormalizedEmail = "SETTINGS.USER@COINBOT.TEST",
                FullName = "Settings User"
            };

            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();

            var service = new UserSettingsService(dbContext, new FakeTimeSyncService(snapshot));
            return new TestHarness(dbContext, user, service);
        }

        public ValueTask DisposeAsync()
        {
            return dbContext.DisposeAsync();
        }
    }

    private sealed class FakeTimeSyncService(BinanceTimeSyncSnapshot? snapshot) : IBinanceTimeSyncService
    {
        private readonly BinanceTimeSyncSnapshot snapshotValue = snapshot ?? new BinanceTimeSyncSnapshot(
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            0,
            10,
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            "Synchronized",
            null);

        public Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(snapshotValue);
        }

        public Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1_710_000_000_000L);
        }
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => false;
    }
}
