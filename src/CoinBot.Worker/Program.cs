using CoinBot.Infrastructure;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
SqlConnectionStringBuilder? sqlConnectionStringBuilder = null;

if (!string.IsNullOrWhiteSpace(connectionString))
{
    try
    {
        sqlConnectionStringBuilder = new SqlConnectionStringBuilder(connectionString);
    }
    catch (ArgumentException)
    {
        sqlConnectionStringBuilder = null;
    }
}

var scannerHandoffEnabled = builder.Configuration.GetValue<bool>("MarketData:Scanner:HandoffEnabled");
var scannerExecutionHost = builder.Configuration.GetValue<string>("MarketData:Scanner:ExecutionHost") ?? "Worker";
var marketDataRestBaseUrl = builder.Configuration.GetValue<string>("MarketData:Binance:RestBaseUrl") ?? "unknown";
var marketDataWebSocketBaseUrl = builder.Configuration.GetValue<string>("MarketData:Binance:WebSocketBaseUrl") ?? "unknown";
var exchangeSyncRestBaseUrl = builder.Configuration.GetValue<string>("ExchangeSync:Binance:RestBaseUrl") ?? "unknown";
var exchangeSyncWebSocketBaseUrl = builder.Configuration.GetValue<string>("ExchangeSync:Binance:WebSocketBaseUrl") ?? "unknown";
var contentRootPath = builder.Environment.ContentRootPath;
var appBaseDirectory = AppContext.BaseDirectory;
var processPath = Environment.ProcessPath ?? "unknown";

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
host.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("CoinBot.Worker.Startup")
    .LogInformation(
        "Runtime configuration resolved. Environment={EnvironmentName} JobOrchestrationEnabled={JobOrchestrationEnabled} MarketScannerEnabled={MarketScannerEnabled} ScannerHandoffEnabled={ScannerHandoffEnabled} ScannerExecutionHost={ScannerExecutionHost} MarketDataRestBaseUrl={MarketDataRestBaseUrl} MarketDataWebSocketBaseUrl={MarketDataWebSocketBaseUrl} ExchangeSyncBinanceEnabled={ExchangeSyncBinanceEnabled} ExchangeSyncRestBaseUrl={ExchangeSyncRestBaseUrl} ExchangeSyncWebSocketBaseUrl={ExchangeSyncWebSocketBaseUrl} ContentRootPath={ContentRootPath} AppBaseDirectory={AppBaseDirectory} ProcessPath={ProcessPath} DbProvider={DbProvider} DbServer={DbServer} DbName={DbName}",
        builder.Environment.EnvironmentName,
        builder.Configuration.GetValue<bool>("JobOrchestration:Enabled"),
        builder.Configuration.GetValue<bool>("MarketData:Scanner:Enabled"),
        scannerHandoffEnabled,
        scannerExecutionHost,
        marketDataRestBaseUrl,
        marketDataWebSocketBaseUrl,
        builder.Configuration.GetValue<bool>("ExchangeSync:Binance:Enabled"),
        exchangeSyncRestBaseUrl,
        exchangeSyncWebSocketBaseUrl,
        contentRootPath,
        appBaseDirectory,
        processPath,
        "SqlServer",
        sqlConnectionStringBuilder?.DataSource ?? "unknown",
        sqlConnectionStringBuilder?.InitialCatalog ?? "unknown");
host.Run();
