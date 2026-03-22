using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Mfa;

public sealed class EmailOtpServiceTests
{
    [Fact]
    public async Task IssueAndVerify_ConsumesLatestChallenge()
    {
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-03-22T10:00:00Z"));
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext, timeProvider);

        var issueResult = await service.IssueAsync("user-1", "login");

        Assert.Matches("^[0-9]{6}$", issueResult.Code);
        Assert.True(await service.VerifyAsync("user-1", "login", issueResult.Code));
        Assert.False(await service.VerifyAsync("user-1", "login", issueResult.Code));
    }

    [Fact]
    public async Task Verify_FailsForDifferentPurpose_AndExpiredCode()
    {
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-03-22T10:00:00Z"));
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext, timeProvider);

        var issueResult = await service.IssueAsync("user-1", "login");

        Assert.False(await service.VerifyAsync("user-1", "disable-mfa", issueResult.Code));

        timeProvider.Advance(TimeSpan.FromMinutes(11));

        Assert.False(await service.VerifyAsync("user-1", "login", issueResult.Code));
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private static EmailOtpService CreateService(ApplicationDbContext dbContext, TimeProvider timeProvider)
    {
        return new EmailOtpService(
            dbContext,
            new EphemeralDataProtectionProvider(),
            timeProvider,
            Options.Create(new MfaOptions()));
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => false;
    }
}
