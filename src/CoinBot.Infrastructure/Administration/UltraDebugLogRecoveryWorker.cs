using CoinBot.Application.Abstractions.Administration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Administration;

public sealed class UltraDebugLogRecoveryWorker(
    IUltraDebugLogService ultraDebugLogService,
    ILogger<UltraDebugLogRecoveryWorker> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var snapshot = await ultraDebugLogService.GetSnapshotAsync(cancellationToken);
        logger.LogInformation(
            "Ultra debug log state restored. Enabled={Enabled} ExpiresAtUtc={ExpiresAtUtc} AutoDisabledReason={AutoDisabledReason}.",
            snapshot.IsEnabled,
            snapshot.ExpiresAtUtc,
            snapshot.AutoDisabledReason ?? "none");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
