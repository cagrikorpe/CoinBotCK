using CoinBot.Infrastructure.Mfa;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure.Mfa;

public sealed class TotpServiceTests
{
    [Fact]
    public void GenerateSecret_ReturnsBase32Secret()
    {
        var service = CreateService();

        var secret = service.GenerateSecret();

        Assert.NotEmpty(secret);
        Assert.Matches("^[A-Z2-7]+$", secret);
    }

    [Fact]
    public void VerifyCode_AcceptsCurrentAndDriftWindowCode_AndRejectsTamperedCiphertext()
    {
        var dataProtectionProvider = new EphemeralDataProtectionProvider();
        var currentService = CreateService(
            dataProtectionProvider,
            new AdjustableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(59)),
            new MfaOptions
            {
                TotpCodeLength = 8,
                TotpAllowedTimeDriftSteps = 1
            });
        var driftService = CreateService(
            dataProtectionProvider,
            new AdjustableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(89)),
            new MfaOptions
            {
                TotpCodeLength = 8,
                TotpAllowedTimeDriftSteps = 1
            });

        var protectedSecret = currentService.ProtectSecret("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");

        Assert.True(currentService.VerifyCode(protectedSecret, "94287082"));
        Assert.True(driftService.VerifyCode(protectedSecret, "94287082"));
        Assert.False(currentService.VerifyCode($"{protectedSecret}-tampered", "94287082"));
    }

    private static TotpService CreateService(
        IDataProtectionProvider? dataProtectionProvider = null,
        TimeProvider? timeProvider = null,
        MfaOptions? options = null)
    {
        return new TotpService(
            dataProtectionProvider ?? new EphemeralDataProtectionProvider(),
            timeProvider ?? new AdjustableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(59)),
            Options.Create(options ?? new MfaOptions()));
    }
}
