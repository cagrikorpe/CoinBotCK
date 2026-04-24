using System.Linq;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;

namespace CoinBot.Infrastructure.Administration;

internal static class UltraDebugLogDefaults
{
    internal static readonly Guid SingletonId = new("B6550CA6-7A90-4853-8569-7A7CA31C79E4");
    internal const string ManualDisableReason = "manual_disable";
    internal const string DurationExpiredReason = "duration_expired";
    internal const string RuntimeErrorReason = "runtime_error";
    internal const string DiskPressureReason = "disk_pressure";
    internal const string RuntimeWriteFailureReason = "ultra_runtime_write_failure";
    internal const string SizeLimitExceededReason = "ultra_size_limit_exceeded";
    internal const int NormalBucketRotateThresholdMb = 32;
    internal const int UltraBucketRotateThresholdMb = 32;
    internal const int DiskPressureFreeSpaceThresholdMb = 512;
    internal const int TailPreviewLineCount = 8;
    internal const int TailPreviewMaxFiles = 2;
    internal const int TailPreviewWindowBytes = 128 * 1024;
    internal const int TailPreviewDetailMaxLength = 384;
    internal const int SearchPreviewDefaultTake = 25;
    internal const int SearchPreviewMaxTake = 50;
    internal const int SearchPreviewMaxFiles = 12;
    internal const int SearchPreviewWindowBytes = 256 * 1024;
    internal static readonly string[] CategoryFolders =
    [
        "scanner",
        "strategy",
        "handoff",
        "execution",
        "exchange",
        "runtime"
    ];

    private static readonly UltraDebugLogDurationOption[] DurationOptions =
    [
        new("1h", "1 saat", TimeSpan.FromHours(1)),
        new("3h", "3 saat", TimeSpan.FromHours(3)),
        new("5h", "5 saat", TimeSpan.FromHours(5)),
        new("8h", "8 saat", TimeSpan.FromHours(8)),
        new("12h", "12 saat", TimeSpan.FromHours(12)),
        new("1d", "1 gün", TimeSpan.FromDays(1)),
        new("3d", "3 gün", TimeSpan.FromDays(3)),
        new("5d", "5 gün", TimeSpan.FromDays(5)),
        new("7d", "7 gün", TimeSpan.FromDays(7))
    ];

    private static readonly UltraDebugLogSizeLimitOption[] LogSizeLimitOptions =
    [
        new(128, "128 MB"),
        new(256, "256 MB"),
        new(512, "512 MB"),
        new(1024, "1024 MB"),
        new(2048, "2048 MB"),
        new(4096, "4096 MB")
    ];

    internal static IReadOnlyCollection<UltraDebugLogDurationOption> GetDurationOptions() => DurationOptions;
    internal static IReadOnlyCollection<UltraDebugLogSizeLimitOption> GetLogSizeLimitOptions() => LogSizeLimitOptions;

    internal static bool TryResolveDuration(string? durationKey, out UltraDebugLogDurationOption option)
    {
        option = DurationOptions.SingleOrDefault(item => string.Equals(item.Key, durationKey?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? new UltraDebugLogDurationOption(string.Empty, string.Empty, TimeSpan.Zero);
        return option.Duration > TimeSpan.Zero;
    }

    internal static bool TryResolveLogSizeLimit(int? valueMb, out UltraDebugLogSizeLimitOption option)
    {
        option = LogSizeLimitOptions.SingleOrDefault(item => item.ValueMb == valueMb)
            ?? new UltraDebugLogSizeLimitOption(0, string.Empty);
        return option.ValueMb > 0;
    }

    internal static int GetRotateThresholdMb(string bucketName)
    {
        return string.Equals(bucketName, "ultra_debug", StringComparison.OrdinalIgnoreCase)
            ? UltraBucketRotateThresholdMb
            : NormalBucketRotateThresholdMb;
    }

    internal static UltraDebugLogState CreateEntity()
    {
        return new UltraDebugLogState
        {
            Id = SingletonId,
            IsEnabled = false,
            UpdatedAtUtc = null
        };
    }

    internal static UltraDebugLogSnapshot CreateSnapshot()
    {
        return new UltraDebugLogSnapshot(
            IsEnabled: false,
            StartedAtUtc: null,
            ExpiresAtUtc: null,
            DurationKey: null,
            EnabledByAdminId: null,
            EnabledByAdminEmail: null,
            AutoDisabledReason: null,
            UpdatedAtUtc: null,
            IsPersisted: false,
            NormalLogsLimitMb: null,
            UltraLogsLimitMb: null);
    }
}
