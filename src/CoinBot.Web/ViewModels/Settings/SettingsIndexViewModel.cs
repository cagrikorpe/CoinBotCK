using System.ComponentModel.DataAnnotations;

namespace CoinBot.Web.ViewModels.Settings;

public sealed class SettingsIndexViewModel
{
    public TimeZoneSettingsInputModel Form { get; init; } = new();

    public IReadOnlyCollection<TimeZoneOptionViewModel> TimeZoneOptions { get; init; } = [];

    public ClockDriftInfoViewModel ClockDrift { get; init; } = new(
        "UTC",
        "UTC",
        "Henüz yok",
        "Henüz yok",
        "0 ms",
        "Henüz yok",
        "Henüz yok",
        "Bilinmiyor",
        null,
        "Henüz yok",
        "Henüz yok");

    public MarketDriftGuardInfoViewModel DriftGuard { get; init; } = new(
        "Bilinmiyor",
        "Henüz yok",
        "Henüz yok",
        "Henüz yok",
        "Henüz yok",
        "Henüz yok",
        "Henüz yok",
        "Henüz yok",
        "Market-data heartbeat (binance:kline)",
        "Server-time refresh yalnız signed REST offset'ini yeniler.");

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
