using System.ComponentModel.DataAnnotations;

namespace CoinBot.Web.ViewModels.Settings;

public sealed class SettingsIndexViewModel
{
    public TimeZoneSettingsInputModel Form { get; init; } = new();

    public IReadOnlyCollection<TimeZoneOptionViewModel> TimeZoneOptions { get; init; } = [];

    public string? SuccessMessage { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class TimeZoneSettingsInputModel
{
    [Required(ErrorMessage = "Saat dilimi seçimi zorunludur.")]
    [Display(Name = "Saat Dilimi")]
    public string PreferredTimeZoneId { get; set; } = "UTC";
}

public sealed record TimeZoneOptionViewModel(
    string TimeZoneId,
    string DisplayName);

public sealed record ClockDriftInfoViewModel(
    string SelectedTimeZoneId,
    string SelectedTimeZoneDisplayName,
    string LocalAppTimeLabel,
    string ExchangeServerTimeLabel,
    string OffsetLabel,
    string DriftLabel,
    string LastSyncLabel,
    string StatusLabel,
    string? FailureReason,
    string RoundTripLabel,
    string RefreshCadenceLabel);

public sealed record MarketDriftGuardInfoViewModel(
    string ThresholdLabel,
    string StateLabel,
    string ReasonLabel,
    string LatestHeartbeatLabel,
    string LatestDataTimestampLabel,
    string LatestDataAgeLabel,
    string LatestDriftLabel,
    string LastStateChangedLabel,
    string SourceLabel,
    string RetryExpectation);
