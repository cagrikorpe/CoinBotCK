using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Infrastructure.MarketData;
using Microsoft.AspNetCore.SignalR;

namespace CoinBot.Web.Hubs;

public sealed class MarketDataHubBridgeService(
    MarketPriceStreamHub streamHub,
    ISharedSymbolRegistry symbolRegistry,
    IHubContext<MarketDataHub> hubContext,
    ILogger<MarketDataHubBridgeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MarketDataHub bridge started.");

        try
        {
            await foreach (var snapshot in streamHub.SubscribeAsync(Array.Empty<string>(), stoppingToken))
            {
                var metadata = await symbolRegistry.GetSymbolAsync(snapshot.Symbol, stoppingToken);
                var payload = MarketDataHub.CreateSnapshot(snapshot.Symbol, snapshot, metadata);

                await hubContext.Clients
                    .Group(MarketDataHub.CreateGroupName(snapshot.Symbol))
                    .SendAsync("marketPriceUpdated", payload, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
