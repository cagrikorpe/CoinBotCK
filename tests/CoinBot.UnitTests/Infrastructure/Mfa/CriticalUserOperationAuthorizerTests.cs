using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Mfa;

public sealed class CriticalUserOperationAuthorizerTests
{
    [Fact]
    public async Task AuthorizeAsync_ReturnsMfaRequired_WhenCurrentUserHasNoMfa()
    {
        await using var context = CreateContext("user-mfa-off");
        context.Users.Add(new ApplicationUser
        {
            Id = "user-mfa-off",
            UserName = "user.mfa.off@coinbot.test",
            NormalizedUserName = "USER.MFA.OFF@COINBOT.TEST",
            Email = "user.mfa.off@coinbot.test",
            NormalizedEmail = "USER.MFA.OFF@COINBOT.TEST",
            FullName = "Mfa Off"
        });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var result = await service.AuthorizeAsync(
            new CriticalUserOperationAuthorizationRequest(
                "user-mfa-off",
                "user:user-mfa-off",
                "ExchangeCredentials.ConnectBinance",
                "User/user-mfa-off/ExchangeCredentials",
                "corr-mfa-off"));

        var audit = await context.AuditLogs.SingleAsync();

        Assert.False(result.IsAuthorized);
        Assert.Equal("MfaRequired", result.FailureCode);
        Assert.Equal("Security.MfaRequired", audit.Action);
        Assert.Contains("ExchangeCredentials.ConnectBinance", audit.Context ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorizeAsync_ReturnsAuthorized_WhenCurrentUserHasTotpMfa()
    {
        await using var context = CreateContext("user-mfa-on");
        context.Users.Add(new ApplicationUser
        {
            Id = "user-mfa-on",
            UserName = "user.mfa.on@coinbot.test",
            NormalizedUserName = "USER.MFA.ON@COINBOT.TEST",
            Email = "user.mfa.on@coinbot.test",
            NormalizedEmail = "USER.MFA.ON@COINBOT.TEST",
            FullName = "Mfa On",
            MfaEnabled = true,
            TotpEnabled = true,
            TwoFactorEnabled = true,
            PreferredMfaProvider = "authenticator-app"
        });
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var result = await service.AuthorizeAsync(
            new CriticalUserOperationAuthorizationRequest(
                "user-mfa-on",
                "user:user-mfa-on",
                "Bots.Update",
                "TradingBot/123",
                "corr-mfa-on"));

        Assert.True(result.IsAuthorized);
        Assert.Empty(context.AuditLogs);
    }

    [Fact]
    public async Task AuthorizeAsync_ReturnsOwnershipViolation_WhenRequestedUserIsOutsideCurrentScope()
    {
        await using var context = CreateContext("user-scope-a");
        var service = CreateService(context);

        var result = await service.AuthorizeAsync(
            new CriticalUserOperationAuthorizationRequest(
                "user-scope-b",
                "user:user-scope-a",
                "Bots.Update",
                "TradingBot/456",
                "corr-scope"));

        var audit = await context.AuditLogs.SingleAsync();

        Assert.False(result.IsAuthorized);
        Assert.Equal("OwnershipViolation", result.FailureCode);
        Assert.Equal("Security.OwnershipViolation", audit.Action);
        Assert.Contains("UserScopeMismatch", audit.Context ?? string.Empty, StringComparison.Ordinal);
    }

    private static CriticalUserOperationAuthorizer CreateService(ApplicationDbContext context)
    {
        return new CriticalUserOperationAuthorizer(
            context,
            new AuditLogService(context, new CorrelationContextAccessor()));
    }

    private static ApplicationDbContext CreateContext(string currentUserId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext(currentUserId));
    }

    private sealed class TestDataScopeContext(string userId) : IDataScopeContext
    {
        public string? UserId => userId;

        public bool HasIsolationBypass => false;
    }
}
