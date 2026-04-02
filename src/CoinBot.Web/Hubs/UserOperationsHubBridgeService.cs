using CoinBot.Infrastructure.Dashboard;
using Microsoft.AspNetCore.SignalR;

namespace CoinBot.Web.Hubs;

public sealed class UserOperationsHubBridgeService(
    UserOperationsStreamHub streamHub,
    IHubContext<UserOperationsHub> hubContext,
    ILogger<UserOperationsHubBridgeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("User operations bridge started.");

        try
        {
            await foreach (var update in streamHub.SubscribeAsync(stoppingToken))
            {
                if (string.Equals(update.OwnerUserId, "*", StringComparison.Ordinal))
                {
                    await hubContext.Clients.All.SendAsync("operationsUpdated", update, stoppingToken);
                }
                else
                {
                    await hubContext.Clients.User(update.OwnerUserId).SendAsync("operationsUpdated", update, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
