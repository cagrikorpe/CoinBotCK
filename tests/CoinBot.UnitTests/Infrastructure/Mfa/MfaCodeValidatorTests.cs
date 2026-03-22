using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Mfa;

public sealed class MfaCodeValidatorTests
{
    [Fact]
    public async Task ValidateAsync_UsesConfiguredProvider_AndFailsClosedWhenDisabled()
    {
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(59));
        await using var dbContext = CreateDbContext();
        var totpService = new TotpService(
            new EphemeralDataProtectionProvider(),
            timeProvider,
            Options.Create(new MfaOptions { TotpCodeLength = 8 }));
        var emailOtpService = new EmailOtpService(
            dbContext,
            new EphemeralDataProtectionProvider(),
            timeProvider,
            Options.Create(new MfaOptions()));
        var validator = new MfaCodeValidator(totpService, emailOtpService);
        var user = new ApplicationUser
        {
            Id = "user-1",
            MfaEnabled = true,
            TotpEnabled = true,
            TotpSecretCiphertext = totpService.ProtectSecret("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ")
        };

        Assert.True(await validator.ValidateAsync(user, MfaProviders.AuthenticatorApp, "94287082"));

        user.MfaEnabled = false;

        Assert.False(await validator.ValidateAsync(user, MfaProviders.AuthenticatorApp, "94287082"));
    }

    [Fact]
    public async Task ValidateAsync_UsesEmailOtpFlow_AndRejectsMissingPurpose()
    {
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.Parse("2026-03-22T10:00:00Z"));
        await using var dbContext = CreateDbContext();
        var totpService = new TotpService(
            new EphemeralDataProtectionProvider(),
            timeProvider,
            Options.Create(new MfaOptions()));
        var emailOtpService = new EmailOtpService(
            dbContext,
            new EphemeralDataProtectionProvider(),
            timeProvider,
            Options.Create(new MfaOptions()));
        var validator = new MfaCodeValidator(totpService, emailOtpService);
        var user = new ApplicationUser
        {
            Id = "user-2",
            MfaEnabled = true,
            EmailOtpEnabled = true
        };

        var issueResult = await emailOtpService.IssueAsync(user.Id, "login");

        Assert.True(await validator.ValidateAsync(user, MfaProviders.EmailOtp, issueResult.Code, "login"));
        Assert.False(await validator.ValidateAsync(user, MfaProviders.EmailOtp, issueResult.Code));
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

        public bool HasIsolationBypass => false;
    }
}
