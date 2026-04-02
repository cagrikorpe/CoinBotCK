using CoinBot.Application.Abstractions.Alerts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Alerts;

public sealed class AlertDispatchCoordinator(
    IServiceScopeFactory serviceScopeFactory,
    IMemoryCache memoryCache,
    ILogger<AlertDispatchCoordinator> logger) : IAlertDispatchCoordinator
{
    public async Task SendAsync(
        AlertNotification notification,
        string dedupeKey,
        TimeSpan cooldown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (string.IsNullOrWhiteSpace(dedupeKey))
        {
            throw new ArgumentException("A dedupe key is required.", nameof(dedupeKey));
        }

        var normalizedDedupeKey = dedupeKey.Trim();

        if (cooldown > TimeSpan.Zero &&
            memoryCache.TryGetValue(normalizedDedupeKey, out _))
        {
            logger.LogDebug(
                "Operational alert {AlertCode} skipped because dedupe key {DedupeKey} is cooling down.",
                notification.Code,
                normalizedDedupeKey);
            return;
        }

        if (cooldown > TimeSpan.Zero)
        {
            memoryCache.Set(normalizedDedupeKey, true, cooldown);
        }

        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var alertService = scope.ServiceProvider.GetRequiredService<IAlertService>();
            await alertService.SendAsync(notification, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Operational alert dispatch failed for code {AlertCode}.",
                notification.Code);
        }
    }
}
