using System.Security.Claims;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Mfa;
using CoinBot.Application.Abstractions.Settings;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Web.Controllers;
using CoinBot.Web.ViewModels.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Web;

public sealed class SettingsControllerTests
{
    [Fact]
    public async Task Index_ReturnsViewModel_FromSettingsService()
    {
        var settingsService = new FakeUserSettingsService();
        var controller = CreateController(settingsService, "settings-user-01", "trace-settings-001");

        var result = await controller.Index(CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SettingsIndexViewModel>(viewResult.Model);

        Assert.Equal(settingsService.Snapshot.PreferredTimeZoneId, model.Form.PreferredTimeZoneId);
        Assert.NotEmpty(model.TimeZoneOptions);
        Assert.Equal("2000 ms", model.DriftGuard.ThresholdLabel);
        Assert.Contains("signed REST", model.DriftGuard.RetryExpectation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Index_Post_PersistsTimeZone_AndRedirectsOnSuccess()
    {
        var settingsService = new FakeUserSettingsService();
        var controller = CreateController(settingsService, "settings-user-02", "trace-settings-002");
        var form = new TimeZoneSettingsInputModel
        {
            PreferredTimeZoneId = settingsService.Snapshot.TimeZoneOptions.Last().TimeZoneId
        };

        var result = await controller.Index(form, CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var request = Assert.Single(settingsService.SaveRequests);

        Assert.Equal(nameof(SettingsController.Index), redirectResult.ActionName);
        Assert.Equal(form.PreferredTimeZoneId, request.Command.PreferredTimeZoneId);
        Assert.Equal("user:settings-user-02", request.Actor);
    }

    [Fact]
    public async Task Index_Post_ReturnsView_AndPreservesSelectedTimeZone_WhenModelStateIsInvalid()
    {
        var settingsService = new FakeUserSettingsService();
        var controller = CreateController(settingsService, "settings-user-03", "trace-settings-003");
        controller.ModelState.AddModelError("Form.PreferredTimeZoneId", "required");
        var form = new TimeZoneSettingsInputModel
        {
            PreferredTimeZoneId = settingsService.Snapshot.TimeZoneOptions.Last().TimeZoneId
        };

        var result = await controller.Index(form, CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<SettingsIndexViewModel>(viewResult.Model);

        Assert.Equal(form.PreferredTimeZoneId, model.Form.PreferredTimeZoneId);
        Assert.Empty(settingsService.SaveRequests);
    }

    [Fact]
    public void Controller_RequiresAuthorize_AndPostRequiresAntiForgery()
    {
        var authorizeAttribute = Assert.Single(
            typeof(SettingsController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>());
        var antiForgeryAttribute = Assert.Single(
            typeof(SettingsController)
                .GetMethod(nameof(SettingsController.Index), [typeof(TimeZoneSettingsInputModel), typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());
        var refreshAntiForgeryAttribute = Assert.Single(
            typeof(SettingsController)
                .GetMethod(nameof(SettingsController.RefreshClockDrift), [typeof(CancellationToken)])!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Cast<ValidateAntiForgeryTokenAttribute>());

        Assert.NotNull(authorizeAttribute);
        Assert.NotNull(antiForgeryAttribute);
        Assert.NotNull(refreshAntiForgeryAttribute);
    }

    [Fact]
    public async Task RefreshClockDrift_ForceRefreshesSync_AndRedirects()
    {
        var settingsService = new FakeUserSettingsService();
        var timeSyncService = new FakeBinanceTimeSyncService
        {
            ForcedSnapshot = new BinanceTimeSyncSnapshot(
                new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 2, 10, 0, 0, 600, DateTimeKind.Utc),
                600,
                22,
                new DateTime(2026, 4, 2, 10, 0, 1, DateTimeKind.Utc),
                "Synchronized",
                null)
        };
        var controller = CreateController(settingsService, "settings-user-04", "trace-settings-004", timeSyncService: timeSyncService);

        var result = await controller.RefreshClockDrift(CancellationToken.None);

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.Equal(nameof(SettingsController.Index), redirectResult.ActionName);
        Assert.Single(timeSyncService.ForceRefreshCalls);
        Assert.Equal("Binance server time sync yenilendi. Son probe drift 600 ms. Market heartbeat drift guard ayrı değerlendirilir.", controller.TempData["SettingsSuccess"]);
    }

    private static SettingsController CreateController(
        FakeUserSettingsService userSettingsService,
        string userId,
        string traceIdentifier,
        FakeBinanceTimeSyncService? timeSyncService = null,
        FakeDataLatencyCircuitBreaker? dataLatencyCircuitBreaker = null)
    {
        var controller = new SettingsController(
            new FakeMfaManagementService(),
            userSettingsService,
            timeSyncService ?? new FakeBinanceTimeSyncService(),
            dataLatencyCircuitBreaker ?? new FakeDataLatencyCircuitBreaker(),
            Options.Create(new DataLatencyGuardOptions
            {
                ClockDriftThresholdSeconds = 2,
                StaleDataThresholdSeconds = 3,
                StopDataThresholdSeconds = 6
            }),
            Options.Create(new BinancePrivateDataOptions
            {
                ServerTimeSyncRefreshSeconds = 30
            }));
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = traceIdentifier;
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId)],
                "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        controller.TempData = new TempDataDictionary(httpContext, new TestTempDataProvider());
        return controller;
    }

    private sealed class FakeUserSettingsService : IUserSettingsService
    {
        public UserSettingsSnapshot Snapshot { get; } = new(
            "UTC",
            "UTC",
            "UTC",
            [
                new UserTimeZoneOptionSnapshot("UTC", "UTC"),
                new UserTimeZoneOptionSnapshot("W. Europe Standard Time", "Berlin")
            ],
            new BinanceTimeSyncSnapshot(
                new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                0,
                12,
                new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                "Synchronized",
                null));

        public List<SaveRequest> SaveRequests { get; } = [];

        public Task<UserSettingsSnapshot?> GetAsync(string userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UserSettingsSnapshot?>(Snapshot);
        }

        public Task<UserSettingsSaveResult> SaveAsync(string userId, UserSettingsSaveCommand command, string actor, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            SaveRequests.Add(new SaveRequest(userId, command, actor, correlationId));
            return Task.FromResult(new UserSettingsSaveResult(true, command.PreferredTimeZoneId, command.PreferredTimeZoneId, null, null));
        }
    }

    private sealed class FakeMfaManagementService : IMfaManagementService
    {
        public Task<MfaStatusSnapshot> GetStatusAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MfaAuthenticatorSetupSnapshot?> GetAuthenticatorSetupAsync(string userId, bool createIfMissing = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> EnableAuthenticatorAsync(string userId, string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DisableAsync(string userId, string verificationCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(string userId, string verificationCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> VerifyAsync(string userId, string provider, string code, string? purpose = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryRedeemRecoveryCodeAsync(string userId, string recoveryCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed record SaveRequest(
        string UserId,
        UserSettingsSaveCommand Command,
        string Actor,
        string? CorrelationId);

    private sealed class FakeBinanceTimeSyncService : IBinanceTimeSyncService
    {
        public List<bool> ForceRefreshCalls { get; } = [];

        public BinanceTimeSyncSnapshot Snapshot { get; init; } = new(
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            0,
            12,
            new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
            "Synchronized",
            null);

        public BinanceTimeSyncSnapshot? ForcedSnapshot { get; init; }

        public Task<BinanceTimeSyncSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            ForceRefreshCalls.Add(forceRefresh);
            return Task.FromResult(forceRefresh && ForcedSnapshot is not null ? ForcedSnapshot : Snapshot);
        }

        public Task<long> GetCurrentTimestampMillisecondsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1_710_000_000_000L);
        }
    }

    private sealed class FakeDataLatencyCircuitBreaker : IDataLatencyCircuitBreaker
    {
        public Task<DegradedModeSnapshot> GetSnapshotAsync(string? correlationId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DegradedModeSnapshot(
                DegradedModeStateCode.Stopped,
                DegradedModeReasonCode.ClockDriftExceeded,
                SignalFlowBlocked: true,
                ExecutionFlowBlocked: true,
                LatestDataTimestampAtUtc: new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc),
                LatestHeartbeatReceivedAtUtc: new DateTime(2026, 4, 2, 10, 0, 2, DateTimeKind.Utc),
                LatestDataAgeMilliseconds: 2200,
                LatestClockDriftMilliseconds: 2234,
                LastStateChangedAtUtc: new DateTime(2026, 4, 2, 10, 0, 3, DateTimeKind.Utc),
                IsPersisted: true));
        }

        public Task<DegradedModeSnapshot> RecordHeartbeatAsync(DataLatencyHeartbeat heartbeat, string? correlationId = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);

        public IDictionary<string, object> LoadTempData(HttpContext context)
        {
            return new Dictionary<string, object>(values, StringComparer.Ordinal);
        }

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            this.values.Clear();

            foreach (var pair in values)
            {
                this.values[pair.Key] = pair.Value;
            }
        }
    }
}
