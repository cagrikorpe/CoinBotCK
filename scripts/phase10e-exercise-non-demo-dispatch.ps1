[CmdletBinding()]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [guid]$StrategyId = "ECF74430-966C-49CA-8EC9-7F31E8F63350",
    [string]$Symbol = "SOLUSDT",
    [ValidateSet("Buy", "Sell")]
    [string]$EntrySide = "Buy",
    [string]$FuturesRestBaseUrl = "https://testnet.binancefuture.com",
    [string]$FuturesWebSocketBaseUrl = "wss://fstream.binancefuture.com",
    [string]$RuntimeMarkerPath = "",
    [string]$RunnerRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($RuntimeMarkerPath)) {
    $RuntimeMarkerPath = Join-Path $repoRoot "artifacts\phase10d-worker-runtime.json"
}

if ([string]::IsNullOrWhiteSpace($RunnerRoot)) {
    $RunnerRoot = Join-Path $repoRoot "artifacts\phase10e-dispatch-runner"
}

function Test-TestnetEndpoint {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }

    try {
        $uri = [Uri]$Value
    }
    catch {
        return $false
    }

    $endpointHost = $uri.Host.ToLowerInvariant()
    $blockedHosts = @(
        "api.binance.com",
        "fapi.binance.com",
        "dapi.binance.com",
        "stream.binance.com",
        "fstream.binance.com"
    )

    if ($blockedHosts -contains $endpointHost) {
        return $false
    }

    return $endpointHost -eq "localhost" -or
        $endpointHost -eq "127.0.0.1" -or
        $endpointHost -eq "::1" -or
        $endpointHost.Contains("testnet") -or
        $endpointHost.Contains("proxy-testnet") -or
        $endpointHost -eq "binancefuture.com" -or
        $endpointHost.EndsWith(".binancefuture.com")
}

if (-not (Test-TestnetEndpoint $FuturesRestBaseUrl) -or -not (Test-TestnetEndpoint $FuturesWebSocketBaseUrl)) {
    throw "Testnet-only endpoint guard rejected the configured endpoint. Dispatch exercise was not started."
}

New-Item -ItemType Directory -Path $RunnerRoot -Force | Out-Null
$runnerProject = Join-Path $RunnerRoot "Phase10eDispatchRunner.csproj"
$runnerProgram = Join-Path $RunnerRoot "Program.cs"

Set-Content -LiteralPath $runnerProject -Encoding UTF8 -Value @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UserSecretsId>016c8a65-b0e7-404b-a04c-0a51f7bea920</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\CoinBot.Infrastructure\CoinBot.Infrastructure.csproj" />
  </ItemGroup>
</Project>
"@

Set-Content -LiteralPath $runnerProgram -Encoding UTF8 -Value @'
using System.Globalization;
using System.Text.Json;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

static string RequiredEnv(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"{name} is required.");
    }

    return value.Trim();
}

static string ResolveBaseAsset(string symbol)
{
    return symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
        ? symbol[..^4].ToUpperInvariant()
        : symbol.ToUpperInvariant();
}

static string ResolveQuoteAsset(string symbol)
{
    return symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase)
        ? "USDT"
        : "USDT";
}

static async Task WarmSharedMarketDataAsync(IServiceProvider services, string symbol, string timeframe)
{
    var now = DateTime.UtcNow;
    var historicalClient = services.GetRequiredService<IBinanceHistoricalKlineClient>();
    var candles = await historicalClient.GetClosedCandlesAsync(symbol, timeframe, now.AddMinutes(-10), now, limit: 10);
    var latest = candles.OrderByDescending(snapshot => snapshot.CloseTimeUtc).FirstOrDefault()
        ?? throw new InvalidOperationException("No testnet kline snapshot was available for market-data warmup.");

    var receivedAtUtc = DateTime.UtcNow;
    var warmedKline = latest with
    {
        ReceivedAtUtc = receivedAtUtc,
        Source = "Binance.Rest.Kline.Phase10e"
    };

    var cache = services.GetRequiredService<ISharedMarketDataCache>();
    var policy = services.GetRequiredService<MarketDataCachePolicyProvider>();
    var cachedAtUtc = DateTime.UtcNow;
    var klineWrite = await cache.WriteAsync(
        new SharedMarketDataCacheEntry<MarketCandleSnapshot>(
            SharedMarketDataCacheDataType.Kline,
            warmedKline.Symbol,
            warmedKline.Interval,
            UpdatedAtUtc: warmedKline.ReceivedAtUtc,
            CachedAtUtc: cachedAtUtc,
            FreshUntilUtc: warmedKline.ReceivedAtUtc.Add(policy.GetFreshness(SharedMarketDataCacheDataType.Kline)),
            ExpiresAtUtc: cachedAtUtc.Add(policy.GetRetention(SharedMarketDataCacheDataType.Kline)),
            Source: warmedKline.Source,
            Payload: warmedKline));

    if (klineWrite.Status != SharedMarketDataCacheWriteStatus.Written)
    {
        throw new InvalidOperationException($"Shared kline warmup failed with {klineWrite.ReasonCode}.");
    }

    var ticker = new MarketPriceSnapshot(
        warmedKline.Symbol,
        warmedKline.ClosePrice,
        warmedKline.CloseTimeUtc,
        warmedKline.ReceivedAtUtc,
        "Binance.Rest.Ticker.Phase10e");

    var tickerWrite = await cache.WriteAsync(
        new SharedMarketDataCacheEntry<MarketPriceSnapshot>(
            SharedMarketDataCacheDataType.Ticker,
            ticker.Symbol,
            Timeframe: null,
            UpdatedAtUtc: ticker.ReceivedAtUtc,
            CachedAtUtc: cachedAtUtc,
            FreshUntilUtc: ticker.ReceivedAtUtc.Add(policy.GetFreshness(SharedMarketDataCacheDataType.Ticker)),
            ExpiresAtUtc: cachedAtUtc.Add(policy.GetRetention(SharedMarketDataCacheDataType.Ticker)),
            Source: ticker.Source,
            Payload: ticker));

    if (tickerWrite.Status != SharedMarketDataCacheWriteStatus.Written)
    {
        throw new InvalidOperationException($"Shared ticker warmup failed with {tickerWrite.ReasonCode}.");
    }
}

var userId = RequiredEnv("PHASE10E_USER_ID");
var botId = Guid.Parse(RequiredEnv("PHASE10E_BOT_ID"));
var strategyId = Guid.Parse(RequiredEnv("PHASE10E_STRATEGY_ID"));
var symbol = RequiredEnv("PHASE10E_SYMBOL").ToUpperInvariant();
var entrySide = Enum.Parse<ExecutionOrderSide>(RequiredEnv("PHASE10E_ENTRY_SIDE"), ignoreCase: true);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRouting();
builder.Services.AddInfrastructure(builder.Configuration);

using var host = builder.Build();
using var scope = host.Services.CreateScope();

var dataScopeAccessor = scope.ServiceProvider.GetRequiredService<IDataScopeContextAccessor>();
using var dataScope = dataScopeAccessor.BeginScope(userId, hasIsolationBypass: true);

var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
var bot = await dbContext.TradingBots
    .IgnoreQueryFilters()
    .AsNoTracking()
    .SingleAsync(entity => entity.Id == botId && entity.OwnerUserId == userId && !entity.IsDeleted);

var strategy = await dbContext.TradingStrategies
    .IgnoreQueryFilters()
    .AsNoTracking()
    .SingleAsync(entity => entity.Id == strategyId && entity.OwnerUserId == userId && !entity.IsDeleted);

if (!strategy.ActiveTradingStrategyVersionId.HasValue)
{
    throw new InvalidOperationException("Strategy active version is missing.");
}

var activeVersion = await dbContext.TradingStrategyVersions
    .IgnoreQueryFilters()
    .AsNoTracking()
    .SingleAsync(entity =>
        entity.Id == strategy.ActiveTradingStrategyVersionId.Value &&
        entity.TradingStrategyId == strategyId &&
        !entity.IsDeleted);

if (!bot.ExchangeAccountId.HasValue)
{
    throw new InvalidOperationException("Bot exchange account is missing.");
}

var referencePrice = await dbContext.HistoricalMarketCandles
    .IgnoreQueryFilters()
    .AsNoTracking()
    .Where(entity => entity.Symbol == symbol && !entity.IsDeleted)
    .OrderByDescending(entity => entity.CloseTimeUtc)
    .Select(entity => entity.ClosePrice)
    .FirstOrDefaultAsync();

if (referencePrice <= 0m)
{
    referencePrice = 1m;
}

var quantity = bot.Quantity.GetValueOrDefault();
if (quantity <= 0m)
{
    quantity = 0.06m;
}

var correlationId = "phase10e-" + Guid.NewGuid().ToString("N");
await WarmSharedMarketDataAsync(scope.ServiceProvider, symbol, "1m");

var command = new ExecutionCommand(
    Actor: "system:phase10e-testnet-dispatch",
    OwnerUserId: userId,
    TradingStrategyId: strategy.Id,
    TradingStrategyVersionId: activeVersion.Id,
    StrategySignalId: Guid.NewGuid(),
    SignalType: StrategySignalType.Entry,
    StrategyKey: bot.StrategyKey,
    Symbol: symbol,
    Timeframe: "1m",
    BaseAsset: ResolveBaseAsset(symbol),
    QuoteAsset: ResolveQuoteAsset(symbol),
    Side: entrySide,
    OrderType: ExecutionOrderType.Market,
    Quantity: quantity,
    Price: referencePrice,
    BotId: bot.Id,
    ExchangeAccountId: bot.ExchangeAccountId,
    IsDemo: false,
    IdempotencyKey: "phase10e:" + Guid.NewGuid().ToString("N"),
    CorrelationId: correlationId,
    ParentCorrelationId: null,
    Context: "Phase10eDeterministicDispatch=True | DevelopmentFuturesTestnetPilot=True | PilotActivationEnabled=True | PilotMarginType=ISOLATED | PilotLeverage=1",
    Plane: ExchangeDataPlane.Futures);

var executionEngine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
var result = await executionEngine.DispatchAsync(command);
var order = result.Order;

Console.WriteLine(JsonSerializer.Serialize(new
{
    Result = "Dispatched",
    OrderId = order.ExecutionOrderId,
    Environment = order.ExecutionEnvironment.ToString(),
    ExecutorKind = order.ExecutorKind.ToString(),
    State = order.State.ToString(),
    FailureCode = order.FailureCode,
    IsDuplicate = result.IsDuplicate
}));
'@

$startedAtUtc = [DateTime]::UtcNow
$connectionString = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
$envVars = [ordered]@{
    DOTNET_ENVIRONMENT = "Development"
    ASPNETCORE_ENVIRONMENT = "Development"
    DOTNET_CLI_HOME = (Join-Path $repoRoot ".dotnet")
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    DOTNET_NOLOGO = "1"
    ConnectionStrings__DefaultConnection = $connectionString
    MarketData__Binance__Enabled = "true"
    MarketData__Binance__RestBaseUrl = $FuturesRestBaseUrl
    MarketData__Binance__WebSocketBaseUrl = $FuturesWebSocketBaseUrl
    ExchangeSync__Binance__Enabled = "true"
    ExchangeSync__Binance__RestBaseUrl = $FuturesRestBaseUrl
    ExchangeSync__Binance__WebSocketBaseUrl = $FuturesWebSocketBaseUrl
    BotExecutionPilot__Enabled = "true"
    BotExecutionPilot__PilotActivationEnabled = "true"
    BotExecutionPilot__AllowGlobalSwitchBypass = "false"
    BotExecutionPilot__SignalEvaluationMode = "Live"
    BotExecutionPilot__ExecutionDispatchMode = "Live"
    BotExecutionPilot__DefaultSymbol = $Symbol
    BotExecutionPilot__Timeframe = "1m"
    BotExecutionPilot__DefaultLeverage = "1"
    BotExecutionPilot__DefaultMarginType = "ISOLATED"
    BotExecutionPilot__AllowedUserIds__0 = $UserId
    BotExecutionPilot__AllowedBotIds__0 = $BotId.ToString("N")
    BotExecutionPilot__AllowedSymbols__0 = $Symbol
    BotExecutionPilot__MaxPilotOrderNotional = "250"
    BotExecutionPilot__MaxOpenPositionsPerUser = "1"
    BotExecutionPilot__PerBotCooldownSeconds = "1"
    BotExecutionPilot__PerSymbolCooldownSeconds = "1"
    BotExecutionPilot__PrivatePlaneFreshnessThresholdSeconds = "120"
    PHASE10E_USER_ID = $UserId
    PHASE10E_BOT_ID = $BotId.ToString()
    PHASE10E_STRATEGY_ID = $StrategyId.ToString()
    PHASE10E_SYMBOL = $Symbol
    PHASE10E_ENTRY_SIDE = $EntrySide
}

$previousValues = @{}
foreach ($key in $envVars.Keys) {
    $previousValues[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
    [Environment]::SetEnvironmentVariable($key, [string]$envVars[$key], "Process")
}

try {
    $runnerOutput = & dotnet run --project $runnerProject -c Debug --no-launch-profile 2>&1
    $runnerExitCode = $LASTEXITCODE
}
finally {
    foreach ($key in $previousValues.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousValues[$key], "Process")
    }
}

$markerDirectory = Split-Path $RuntimeMarkerPath -Parent
if (-not [string]::IsNullOrWhiteSpace($markerDirectory)) {
    New-Item -ItemType Directory -Path $markerDirectory -Force | Out-Null
}

[ordered]@{
    StartedAtUtc = $startedAtUtc.ToString("O")
    Server = $Server
    Database = $Database
    UserId = $UserId
    BotId = $BotId.ToString()
    Symbol = $Symbol
    EntrySide = $EntrySide
    TestnetOnly = $true
    RequireLiveWorker = $false
    ExerciseKind = "DeterministicNonDemoDispatch"
    FuturesRestBaseUrl = $FuturesRestBaseUrl
    FuturesWebSocketBaseUrl = $FuturesWebSocketBaseUrl
    ExecutionDispatchMode = "Live"
    SignalEvaluationMode = "Live"
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RuntimeMarkerPath -Encoding UTF8

if ($runnerExitCode -ne 0) {
    throw ($runnerOutput -join [Environment]::NewLine)
}

$safeOutput = $runnerOutput | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
Write-Output $safeOutput
Write-Output "RuntimeMarkerPath=$RuntimeMarkerPath"
