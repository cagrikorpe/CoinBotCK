[CmdletBinding()]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [guid]$StrategyId = "ECF74430-966C-49CA-8EC9-7F31E8F63350",
    [string]$Symbol = "SOLUSDT",
    [string]$FuturesRestBaseUrl = "https://testnet.binancefuture.com",
    [string]$FuturesWebSocketBaseUrl = "wss://fstream.binancefuture.com",
    [string]$RuntimeMarkerPath = "",
    [string]$RunnerRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($RunnerRoot)) {
    $RunnerRoot = Join-Path $repoRoot "artifacts\phase10j-reduce-only-close-runner"
}
if ([string]::IsNullOrWhiteSpace($RuntimeMarkerPath)) {
    $RuntimeMarkerPath = Join-Path $repoRoot "artifacts\phase10j-close-runtime.json"
}

function Test-TestnetEndpoint {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    try { $uri = [Uri]$Value } catch { return $false }
    $endpointHost = $uri.Host.ToLowerInvariant()
    $blockedHosts = @("api.binance.com", "fapi.binance.com", "dapi.binance.com", "stream.binance.com", "fstream.binance.com")
    if ($blockedHosts -contains $endpointHost) { return $false }
    return $endpointHost -eq "localhost" -or
        $endpointHost -eq "127.0.0.1" -or
        $endpointHost -eq "::1" -or
        $endpointHost.Contains("testnet") -or
        $endpointHost.Contains("proxy-testnet") -or
        $endpointHost -eq "binancefuture.com" -or
        $endpointHost.EndsWith(".binancefuture.com")
}

if (-not (Test-TestnetEndpoint $FuturesRestBaseUrl) -or -not (Test-TestnetEndpoint $FuturesWebSocketBaseUrl)) {
    throw "Testnet-only endpoint guard rejected the configured endpoint. Reduce-only close exercise was not started."
}

New-Item -ItemType Directory -Path $RunnerRoot -Force | Out-Null
$runnerProject = Join-Path $RunnerRoot "Phase10jReduceOnlyCloseRunner.csproj"
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
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Execution;
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
    return symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? "USDT" : "USDT";
}

static string NormalizeSide(string? value)
{
    return string.IsNullOrWhiteSpace(value) ? "BOTH" : value.Trim().ToUpperInvariant();
}

static decimal SignedEntityPositionQuantity(ExchangePosition position)
{
    return NormalizeSide(position.PositionSide) == "SHORT"
        ? -Math.Abs(position.Quantity)
        : position.Quantity;
}

static decimal SignedSnapshotPositionQuantity(ExchangePositionSnapshot position)
{
    return NormalizeSide(position.PositionSide) == "SHORT"
        ? -Math.Abs(position.Quantity)
        : position.Quantity;
}

static string FormatDecimal(decimal value)
{
    return value.ToString("0.##################", CultureInfo.InvariantCulture);
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
        Source = "Binance.Rest.Kline.Phase10j"
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
        "Binance.Rest.Ticker.Phase10j");

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

static string BuildDevelopmentFuturesPilotClientOrderId(Guid orderId)
{
    return "cbp0_" + Convert.ToBase64String(orderId.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

static ExecutionOrderState MapExchangeStatus(string status)
{
    return status.Trim().ToUpperInvariant() switch
    {
        "NEW" => ExecutionOrderState.Submitted,
        "PARTIALLY_FILLED" => ExecutionOrderState.PartiallyFilled,
        "FILLED" => ExecutionOrderState.Filled,
        "CANCELED" => ExecutionOrderState.Cancelled,
        "EXPIRED" => ExecutionOrderState.Cancelled,
        "PENDING_CANCEL" => ExecutionOrderState.CancelRequested,
        "REJECTED" => ExecutionOrderState.Rejected,
        _ => ExecutionOrderState.Submitted
    };
}

static string BuildSummary(ExecutionOrder order, BinanceOrderStatusSnapshot snapshot, ExchangeStateDriftStatus status)
{
    var exchangeState = MapExchangeStatus(snapshot.Status);
    return status == ExchangeStateDriftStatus.DriftDetected
        ? $"LocalState={order.State}; ExchangeState={exchangeState}; LocalFilledQuantity={FormatDecimal(order.FilledQuantity)}; ExchangeFilledQuantity={FormatDecimal(snapshot.ExecutedQuantity)}; Source={snapshot.Source}"
        : $"LocalState={order.State}; ExchangeState={exchangeState}; FilledQuantity={FormatDecimal(snapshot.ExecutedQuantity)}; Source={snapshot.Source}";
}

static async Task<BinanceOrderStatusSnapshot?> TryReconcileOrderAsync(
    IServiceProvider services,
    ExecutionOrder order,
    ExchangeCredentialAccessResult credentialAccess)
{
    if (!order.ExchangeAccountId.HasValue || !order.SubmittedToBroker)
    {
        return null;
    }

    var restClient = services.GetRequiredService<IBinancePrivateRestClient>();
    var lifecycleService = services.GetRequiredService<ExecutionOrderLifecycleService>();
    var snapshot = await restClient.GetOrderAsync(
        new BinanceOrderQueryRequest(
            order.ExchangeAccountId.Value,
            order.Symbol,
            order.ExternalOrderId,
            string.IsNullOrWhiteSpace(order.ExternalOrderId) ? BuildDevelopmentFuturesPilotClientOrderId(order.Id) : null,
            credentialAccess.ApiKey,
            credentialAccess.ApiSecret));

    var exchangeState = MapExchangeStatus(snapshot.Status);
    var exchangeFilledQuantity = Math.Min(order.Quantity, snapshot.ExecutedQuantity);
    var status = order.State != exchangeState || order.FilledQuantity != exchangeFilledQuantity
        ? ExchangeStateDriftStatus.DriftDetected
        : ExchangeStateDriftStatus.InSync;

    await lifecycleService.ApplyReconciliationAsync(order.Id, snapshot, status, BuildSummary(order, snapshot, status));
    return snapshot;
}

static async Task<decimal> RefreshAccountSnapshotAsync(
    IServiceProvider services,
    Guid exchangeAccountId,
    string ownerUserId,
    string symbol,
    string correlationId)
{
    var credentialService = services.GetRequiredService<IExchangeCredentialService>();
    var credentialAccess = await credentialService.GetAsync(
        new ExchangeCredentialAccessRequest(
            exchangeAccountId,
            "system:phase10j-reduce-only-close",
            ExchangeCredentialAccessPurpose.Synchronization,
            correlationId));

    var restClient = services.GetRequiredService<IBinancePrivateRestClient>();
    var balanceSync = services.GetRequiredService<ExchangeBalanceSyncService>();
    var positionSync = services.GetRequiredService<ExchangePositionSyncService>();
    var syncStateService = services.GetRequiredService<ExchangeAccountSyncStateService>();

    var accountSnapshot = await restClient.GetAccountSnapshotAsync(
        exchangeAccountId,
        ownerUserId,
        "Binance",
        credentialAccess.ApiKey,
        credentialAccess.ApiSecret);

    await balanceSync.ApplyAsync(accountSnapshot);
    await syncStateService.RecordBalanceSyncAsync(accountSnapshot);
    await positionSync.ApplyAsync(accountSnapshot);
    await syncStateService.RecordPositionSyncAsync(accountSnapshot);

    return accountSnapshot.Positions
        .Where(position => string.Equals(position.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
        .Sum(SignedSnapshotPositionQuantity);
}

var userId = RequiredEnv("PHASE10J_USER_ID");
var botId = Guid.Parse(RequiredEnv("PHASE10J_BOT_ID"));
var strategyId = Guid.Parse(RequiredEnv("PHASE10J_STRATEGY_ID"));
var symbol = RequiredEnv("PHASE10J_SYMBOL").ToUpperInvariant();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRouting();
builder.Services.AddInfrastructure(builder.Configuration);

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var services = scope.ServiceProvider;

var dataScopeAccessor = services.GetRequiredService<IDataScopeContextAccessor>();
using var dataScope = dataScopeAccessor.BeginScope(userId, hasIsolationBypass: true);

var dbContext = services.GetRequiredService<ApplicationDbContext>();
var bot = await dbContext.TradingBots.IgnoreQueryFilters().AsNoTracking()
    .SingleAsync(entity => entity.Id == botId && entity.OwnerUserId == userId && !entity.IsDeleted);

if (!bot.ExchangeAccountId.HasValue)
{
    throw new InvalidOperationException("Bot exchange account is missing.");
}

var strategy = await dbContext.TradingStrategies.IgnoreQueryFilters().AsNoTracking()
    .SingleAsync(entity => entity.Id == strategyId && entity.OwnerUserId == userId && !entity.IsDeleted);

if (!strategy.ActiveTradingStrategyVersionId.HasValue)
{
    throw new InvalidOperationException("Strategy active version is missing.");
}

var activeVersion = await dbContext.TradingStrategyVersions.IgnoreQueryFilters().AsNoTracking()
    .SingleAsync(entity =>
        entity.Id == strategy.ActiveTradingStrategyVersionId.Value &&
        entity.TradingStrategyId == strategyId &&
        !entity.IsDeleted);

await WarmSharedMarketDataAsync(scope.ServiceProvider, symbol, "1m");
var preCloseExchangeNetQuantity = await RefreshAccountSnapshotAsync(
    scope.ServiceProvider,
    bot.ExchangeAccountId.Value,
    userId,
    symbol,
    "phase10j-preclose-sync-" + Guid.NewGuid().ToString("N"));
dbContext.ChangeTracker.Clear();

var positions = await dbContext.ExchangePositions.IgnoreQueryFilters().AsNoTracking()
    .Where(entity =>
        entity.OwnerUserId == userId &&
        entity.ExchangeAccountId == bot.ExchangeAccountId.Value &&
        entity.Plane == ExchangeDataPlane.Futures &&
        entity.Symbol == symbol &&
        entity.Quantity != 0m &&
        !entity.IsDeleted)
    .ToListAsync();

var netQuantity = positions.Sum(SignedEntityPositionQuantity);
if (netQuantity == 0m && preCloseExchangeNetQuantity != 0m)
{
    netQuantity = preCloseExchangeNetQuantity;
}

if (netQuantity == 0m)
{
    throw new InvalidOperationException($"No open live futures position was found for reduce-only close. PreCloseExchangeNetQuantity={FormatDecimal(preCloseExchangeNetQuantity)}.");
}

var closeSide = netQuantity > 0m ? ExecutionOrderSide.Sell : ExecutionOrderSide.Buy;
var closeQuantity = Math.Abs(netQuantity);
var referencePrice = await dbContext.HistoricalMarketCandles.IgnoreQueryFilters().AsNoTracking()
    .Where(entity => entity.Symbol == symbol && !entity.IsDeleted)
    .OrderByDescending(entity => entity.CloseTimeUtc)
    .Select(entity => entity.ClosePrice)
    .FirstOrDefaultAsync();

if (referencePrice <= 0m)
{
    referencePrice = positions.Select(entity => entity.EntryPrice).FirstOrDefault(value => value > 0m);
}

if (referencePrice <= 0m)
{
    referencePrice = 1m;
}

var correlationId = "phase10j-" + Guid.NewGuid().ToString("N");
var command = new ExecutionCommand(
    Actor: "system:phase10j-reduce-only-close",
    OwnerUserId: userId,
    TradingStrategyId: strategy.Id,
    TradingStrategyVersionId: activeVersion.Id,
    StrategySignalId: Guid.NewGuid(),
    SignalType: StrategySignalType.Exit,
    StrategyKey: bot.StrategyKey,
    Symbol: symbol,
    Timeframe: "1m",
    BaseAsset: ResolveBaseAsset(symbol),
    QuoteAsset: ResolveQuoteAsset(symbol),
    Side: closeSide,
    OrderType: ExecutionOrderType.Market,
    Quantity: closeQuantity,
    Price: referencePrice,
    BotId: bot.Id,
    ExchangeAccountId: bot.ExchangeAccountId,
    IsDemo: false,
    IdempotencyKey: "phase10j:close:" + Guid.NewGuid().ToString("N"),
    CorrelationId: correlationId,
    Context: "Phase10jReduceOnlyClose=True | DevelopmentFuturesTestnetPilot=True | PilotActivationEnabled=True | PilotMarginType=ISOLATED | PilotLeverage=1",
    ReduceOnly: true,
    Plane: ExchangeDataPlane.Futures);

var executionEngine = services.GetRequiredService<IExecutionEngine>();
var result = await executionEngine.DispatchAsync(command);
var closeOrderId = result.Order.ExecutionOrderId;

var closeOrder = await dbContext.ExecutionOrders.IgnoreQueryFilters()
    .SingleAsync(entity => entity.Id == closeOrderId && !entity.IsDeleted);

var credentialService = services.GetRequiredService<IExchangeCredentialService>();
var credentialAccess = await credentialService.GetAsync(
    new ExchangeCredentialAccessRequest(
        closeOrder.ExchangeAccountId!.Value,
        "system:phase10j-reduce-only-close",
        ExchangeCredentialAccessPurpose.Synchronization,
        closeOrder.RootCorrelationId));

BinanceOrderStatusSnapshot? orderSnapshot = null;
if (closeOrder.SubmittedToBroker)
{
    orderSnapshot = await TryReconcileOrderAsync(services, closeOrder, credentialAccess);
    closeOrder = await dbContext.ExecutionOrders.IgnoreQueryFilters()
        .SingleAsync(entity => entity.Id == closeOrderId && !entity.IsDeleted);
}

var restClient = services.GetRequiredService<IBinancePrivateRestClient>();
var balanceSync = services.GetRequiredService<ExchangeBalanceSyncService>();
var positionSync = services.GetRequiredService<ExchangePositionSyncService>();
var syncStateService = services.GetRequiredService<ExchangeAccountSyncStateService>();

ExchangeAccountSnapshot accountSnapshot = null!;
decimal exchangeNetQuantity = netQuantity;
for (var attempt = 0; attempt < 6; attempt++)
{
    accountSnapshot = await restClient.GetAccountSnapshotAsync(
        closeOrder.ExchangeAccountId!.Value,
        userId,
        "Binance",
        credentialAccess.ApiKey,
        credentialAccess.ApiSecret);

    await balanceSync.ApplyAsync(accountSnapshot);
    await syncStateService.RecordBalanceSyncAsync(accountSnapshot);
    await positionSync.ApplyAsync(accountSnapshot);
    await syncStateService.RecordPositionSyncAsync(accountSnapshot);

    exchangeNetQuantity = accountSnapshot.Positions
        .Where(entity => string.Equals(entity.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
        .Sum(SignedSnapshotPositionQuantity);

    if (exchangeNetQuantity == 0m)
    {
        break;
    }

    await Task.Delay(TimeSpan.FromSeconds(2));
}

if (orderSnapshot is not null)
{
    closeOrder = await dbContext.ExecutionOrders.IgnoreQueryFilters()
        .SingleAsync(entity => entity.Id == closeOrderId && !entity.IsDeleted);
    await TryReconcileOrderAsync(services, closeOrder, credentialAccess);
}

closeOrder = await dbContext.ExecutionOrders.IgnoreQueryFilters()
    .SingleAsync(entity => entity.Id == closeOrderId && !entity.IsDeleted);
var latestTransition = await dbContext.ExecutionOrderTransitions.IgnoreQueryFilters().AsNoTracking()
    .Where(entity => entity.ExecutionOrderId == closeOrderId && !entity.IsDeleted)
    .OrderByDescending(entity => entity.SequenceNumber)
    .Select(entity => entity.EventCode)
    .FirstOrDefaultAsync();
var refreshedBot = await dbContext.TradingBots.IgnoreQueryFilters().AsNoTracking()
    .SingleAsync(entity => entity.Id == botId && !entity.IsDeleted);

Console.WriteLine(JsonSerializer.Serialize(new
{
    Result = "ReduceOnlyCloseDispatched",
    CloseOrderId = closeOrder.Id,
    Environment = closeOrder.ExecutionEnvironment.ToString(),
    ExecutorKind = closeOrder.ExecutorKind.ToString(),
    State = closeOrder.State.ToString(),
    closeOrder.FailureCode,
    Side = closeOrder.Side.ToString(),
    closeOrder.ReduceOnly,
    closeOrder.FilledQuantity,
    LatestTransition = latestTransition ?? "none",
    PreCloseExchangeNetQuantity = preCloseExchangeNetQuantity,
    ExchangeNetQuantity = exchangeNetQuantity,
    BotOpenPositionCount = refreshedBot.OpenPositionCount,
    BotOpenOrderCount = refreshedBot.OpenOrderCount,
    result.IsDuplicate
}));
'@

$connectionString = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
$startedAtUtc = [DateTime]::UtcNow
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
    PHASE10J_USER_ID = $UserId
    PHASE10J_BOT_ID = $BotId.ToString()
    PHASE10J_STRATEGY_ID = $StrategyId.ToString()
    PHASE10J_SYMBOL = $Symbol
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

if ($runnerExitCode -ne 0) {
    throw ($runnerOutput -join [Environment]::NewLine)
}

$safeOutput = $runnerOutput | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
$jsonLine = $safeOutput | Where-Object { $_.TrimStart().StartsWith("{") } | Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($jsonLine)) {
    throw "Reduce-only close runner did not produce a JSON summary."
}

$summary = $jsonLine | ConvertFrom-Json -ErrorAction Stop
if ($null -eq $summary.CloseOrderId -or [string]::IsNullOrWhiteSpace([string]$summary.CloseOrderId)) {
    throw "Reduce-only close runner did not return a close order id."
}

$markerDirectory = Split-Path $RuntimeMarkerPath -Parent
if (-not [string]::IsNullOrWhiteSpace($markerDirectory)) {
    New-Item -ItemType Directory -Path $markerDirectory -Force | Out-Null
}

[ordered]@{
    StartedAtUtc = $startedAtUtc.ToString("O")
    CloseOrderId = [string]$summary.CloseOrderId
    Server = $Server
    Database = $Database
    UserId = $UserId
    BotId = $BotId.ToString()
    Symbol = $Symbol
    TestnetOnly = $true
    ExerciseKind = "ReduceOnlyClose"
    ExecutionEnvironment = [string]$summary.Environment
    ExecutorKind = [string]$summary.ExecutorKind
    ReduceOnly = [bool]$summary.ReduceOnly
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $RuntimeMarkerPath -Encoding UTF8

Write-Output $safeOutput
Write-Output "RuntimeMarkerPath=$RuntimeMarkerPath"
