using CoinBot.Infrastructure.Jobs;
using CoinBot.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddJobOrchestration(builder.Configuration);
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<JobKeepAliveWorker>();
builder.Services.AddHostedService<JobCleanupWorker>();
builder.Services.AddHostedService<JobWatchdogWorker>();

var host = builder.Build();
host.Run();
