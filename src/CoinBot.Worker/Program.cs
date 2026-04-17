using CoinBot.Infrastructure;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRouting();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJobOrchestration(builder.Configuration);
if (builder.Environment.IsDevelopment())
{
    builder.Services.Configure<HostOptions>(options =>
    {
        options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
        options.ServicesStartConcurrently = true;
    });
}
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<JobKeepAliveWorker>();
builder.Services.AddHostedService<JobCleanupWorker>();
builder.Services.AddHostedService<JobWatchdogWorker>();
builder.Services.AddHostedService<MarketScannerWorker>();

var host = builder.Build();
host.Run();
