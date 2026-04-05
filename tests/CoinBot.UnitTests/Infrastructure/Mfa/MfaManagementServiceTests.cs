using System.Buffers.Binary;
using System.Security.Cryptography;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Mfa;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoinBot.UnitTests.Infrastructure.Mfa;

public sealed class MfaManagementServiceTests
{
    [Fact]
    public async Task GetAuthenticatorSetupAsync_CreatesProtectedPendingSecret_AndDoesNotEnableMfa()
    {
        await using var harness = await TestHarness.CreateAsync();

        var setup = await harness.Service.GetAuthenticatorSetupAsync(harness.User.Id, createIfMissing: true);
        var status = await harness.Service.GetStatusAsync(harness.User.Id);
        var user = await harness.ReloadUserAsync();

        Assert.NotNull(setup);
        Assert.Matches("^[A-Z2-7]+$", setup!.SharedKey);
        Assert.StartsWith("otpauth://totp/", setup.AuthenticatorUri, StringComparison.Ordinal);
        Assert.Equal(setup.SharedKey, (await harness.UserManager.GetAuthenticatorKeyAsync(user))!);
        Assert.False(status.IsMfaEnabled);
        Assert.True(status.HasPendingAuthenticatorEnrollment);
        Assert.False(user.TotpEnabled);
        Assert.False(user.TwoFactorEnabled);
        Assert.NotNull(user.TotpSecretCiphertext);
        Assert.DoesNotContain(setup.SharedKey, user.TotpSecretCiphertext!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnableAuthenticatorAsync_EnablesMfa_GeneratesRecoveryCodes_AndStopsReturningSecret()
    {
        await using var harness = await TestHarness.CreateAsync();

        var setup = await harness.Service.GetAuthenticatorSetupAsync(harness.User.Id, createIfMissing: true)
            ?? throw new InvalidOperationException("Setup should exist.");
        var code = ComputeTotp(setup.SharedKey, harness.UtcNow);

        var recoveryCodes = await harness.Service.EnableAuthenticatorAsync(harness.User.Id, code);
        var status = await harness.Service.GetStatusAsync(harness.User.Id);
        var user = await harness.ReloadUserAsync();
        var postEnableSetup = await harness.Service.GetAuthenticatorSetupAsync(harness.User.Id);

        Assert.NotNull(recoveryCodes);
        Assert.Equal(8, recoveryCodes!.Count);
        Assert.All(recoveryCodes, recoveryCode => Assert.Matches("^[A-Z2-7]{4}-[A-Z2-7]{4}$", recoveryCode));
        Assert.True(status.IsMfaEnabled);
        Assert.True(status.IsTotpEnabled);
        Assert.False(status.HasPendingAuthenticatorEnrollment);
        Assert.Equal(MfaProviders.AuthenticatorApp, status.PreferredProvider);
        Assert.Equal(recoveryCodes.Count, status.ActiveRecoveryCodeCount);
        Assert.True(user.MfaEnabled);
        Assert.True(user.TotpEnabled);
        Assert.True(user.TwoFactorEnabled);
        Assert.NotNull(user.TotpSecretCiphertext);
        Assert.Null(postEnableSetup);

        var storedCodeHash = await harness.DbContext.MfaRecoveryCodes
            .Where(entity => entity.UserId == harness.User.Id && !entity.IsDeleted)
            .Select(entity => entity.CodeHash)
            .FirstAsync();

        Assert.DoesNotContain(recoveryCodes[0].Replace("-", string.Empty, StringComparison.Ordinal), storedCodeHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegenerateRecoveryCodesAsync_ReplacesExistingCodes()
    {
        await using var harness = await TestHarness.CreateAsync();

        var setup = await harness.Service.GetAuthenticatorSetupAsync(harness.User.Id, createIfMissing: true)
            ?? throw new InvalidOperationException("Setup should exist.");
        var code = ComputeTotp(setup.SharedKey, harness.UtcNow);
        var initialRecoveryCodes = await harness.Service.EnableAuthenticatorAsync(harness.User.Id, code)
            ?? throw new InvalidOperationException("Enable should succeed.");
        var regeneratedRecoveryCodes = await harness.Service.RegenerateRecoveryCodesAsync(harness.User.Id, code);

        Assert.NotNull(regeneratedRecoveryCodes);
        Assert.Equal(8, regeneratedRecoveryCodes!.Count);
        Assert.DoesNotContain(initialRecoveryCodes[0], regeneratedRecoveryCodes, StringComparer.Ordinal);
        Assert.False(await harness.Service.TryRedeemRecoveryCodeAsync(harness.User.Id, initialRecoveryCodes[0]));
        Assert.True(await harness.Service.TryRedeemRecoveryCodeAsync(harness.User.Id, regeneratedRecoveryCodes[0]));
    }

    [Fact]
    public async Task DisableAsync_AcceptsRecoveryCode_AndClearsMfaState()
    {
        await using var harness = await TestHarness.CreateAsync();

        var setup = await harness.Service.GetAuthenticatorSetupAsync(harness.User.Id, createIfMissing: true)
            ?? throw new InvalidOperationException("Setup should exist.");
        var code = ComputeTotp(setup.SharedKey, harness.UtcNow);
        var recoveryCodes = await harness.Service.EnableAuthenticatorAsync(harness.User.Id, code)
            ?? throw new InvalidOperationException("Enable should succeed.");

        var disabled = await harness.Service.DisableAsync(harness.User.Id, recoveryCodes[0]);
        var status = await harness.Service.GetStatusAsync(harness.User.Id);
        var user = await harness.ReloadUserAsync();

        Assert.True(disabled);
        Assert.False(status.IsMfaEnabled);
        Assert.False(status.IsTotpEnabled);
        Assert.False(status.HasPendingAuthenticatorEnrollment);
        Assert.Equal(0, status.ActiveRecoveryCodeCount);
        Assert.False(user.MfaEnabled);
        Assert.False(user.TotpEnabled);
        Assert.False(user.TwoFactorEnabled);
        Assert.Null(user.TotpSecretCiphertext);
    }

    [Fact]
    public async Task DisableAsync_ReturnsFalse_AndWritesAudit_WhenVerificationCodeIsInvalid()
    {
        await using var harness = await TestHarness.CreateAsync();

        var setup = await harness.Service.GetAuthenticatorSetupAsync(harness.User.Id, createIfMissing: true)
            ?? throw new InvalidOperationException("Setup should exist.");
        var code = ComputeTotp(setup.SharedKey, harness.UtcNow);
        _ = await harness.Service.EnableAuthenticatorAsync(harness.User.Id, code)
            ?? throw new InvalidOperationException("Enable should succeed.");

        var disabled = await harness.Service.DisableAsync(harness.User.Id, "WRNG-WRNG");
        var audit = await harness.DbContext.AuditLogs
            .OrderByDescending(entity => entity.CreatedDate)
            .FirstAsync();

        Assert.False(disabled);
        Assert.Equal("Identity.MfaDisableRejected", audit.Action);
        Assert.Contains("invalid-management-code", audit.Context ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisableAsync_RejectsCrossUserScope()
    {
        var actualUserId = Guid.NewGuid().ToString("N");
        await using var harness = await TestHarness.CreateAsync(userId: actualUserId, scopedUserId: "different-user");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.DisableAsync(actualUserId, "000000"));

        Assert.Contains("outside the authenticated isolation boundary", exception.Message, StringComparison.Ordinal);
    }

    private static string ComputeTotp(string secret, DateTimeOffset utcNow, int digits = 6, int timeStepSeconds = 30)
    {
        var secretBytes = DecodeBase32(secret);
        var counter = utcNow.ToUnixTimeSeconds() / timeStepSeconds;
        Span<byte> counterBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        using var hmac = new HMACSHA1(secretBytes);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0F;
        var binaryCode =
            ((hash[offset] & 0x7F) << 24) |
            (hash[offset + 1] << 16) |
            (hash[offset + 2] << 8) |
            hash[offset + 3];
        var modulo = 1;

        for (var index = 0; index < digits; index++)
        {
            modulo *= 10;
        }

        return (binaryCode % modulo).ToString($"D{digits}");
    }

    private static byte[] DecodeBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var normalized = value.Trim().TrimEnd('=').ToUpperInvariant();
        var output = new List<byte>((normalized.Length * 5) / 8);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var currentChar in normalized)
        {
            var index = alphabet.IndexOf(currentChar);

            if (index < 0)
            {
                throw new FormatException("The provided secret is not valid Base32.");
            }

            buffer = (buffer << 5) | index;
            bitsLeft += 5;

            if (bitsLeft < 8)
            {
                continue;
            }

            output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
            bitsLeft -= 8;
        }

        return [.. output];
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly ServiceProvider serviceProvider;
        private readonly IServiceScope scope;

        private TestHarness(ServiceProvider serviceProvider, IServiceScope scope, ApplicationUser user)
        {
            this.serviceProvider = serviceProvider;
            this.scope = scope;
            User = user;
        }

        public ApplicationDbContext DbContext => scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        public IMfaManagementService Service => scope.ServiceProvider.GetRequiredService<IMfaManagementService>();

        public UserManager<ApplicationUser> UserManager => scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        public ApplicationUser User { get; }

        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-03-23T10:15:00Z");

        public static async Task<TestHarness> CreateAsync(string? userId = null, string? scopedUserId = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDataProtection();
            services.AddSingleton<TimeProvider>(new AdjustableTimeProvider(DateTimeOffset.Parse("2026-03-23T10:15:00Z")));
            services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
            var resolvedUserId = userId ?? Guid.NewGuid().ToString("N");
            services.AddScoped<IDataScopeContext>(_ => new TestDataScopeContext(scopedUserId ?? resolvedUserId));
            services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
            services.AddIdentityCore<ApplicationUser>(options =>
                {
                    options.Tokens.AuthenticatorIssuer = "CoinBot";
                    options.User.RequireUniqueEmail = true;
                })
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();
            services.AddScoped<IAuditLogService, AuditLogService>();
            services.AddScoped<ITotpService, TotpService>();
            services.AddScoped<IEmailOtpService, EmailOtpService>();
            services.AddScoped<IMfaManagementService, MfaManagementService>();
            services.AddOptions<MfaOptions>().Configure(options => { });

            var serviceProvider = services.BuildServiceProvider();
            var scope = serviceProvider.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Id = resolvedUserId,
                UserName = "mfa.user@coinbot.test",
                Email = "mfa.user@coinbot.test",
                FullName = "Mfa User"
            };

            var createResult = await userManager.CreateAsync(user, "Passw0rd!");

            if (!createResult.Succeeded)
            {
                throw new InvalidOperationException("Test user could not be created.");
            }

            return new TestHarness(serviceProvider, scope, user);
        }

        public async Task<ApplicationUser> ReloadUserAsync()
        {
            return await DbContext.Users.SingleAsync(entity => entity.Id == User.Id);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await serviceProvider.DisposeAsync();
        }
    }

    private sealed class TestDataScopeContext(string userId) : IDataScopeContext
    {
        public string? UserId => userId;

        public bool HasIsolationBypass => false;
    }
}
