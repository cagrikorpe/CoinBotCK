[CmdletBinding()]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [guid]$OrderId = [guid]::Empty,
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
    $RunnerRoot = Join-Path $repoRoot "artifacts\phase10i-reconciliation-runner"
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
    throw "Testnet-only endpoint guard rejected the configured endpoint. Reconciliation exercise was not started."
}

New-Item -ItemType Directory -Path $RunnerRoot -Force | Out-Null
$runnerProject = Join-Path $RunnerRoot "Phase10iReconciliationRunner.csproj"
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
using System.Text.Json;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Execution;
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

static string FormatDecimal(decimal value)
{
    return value.ToString("0.##################", System.Globalization.CultureInfo.InvariantCulture);
}

static string BuildDevelopmentFuturesPilotClientOrderId(Guid orderId)
{
    return "cbp0_" + Convert.ToBase64String(orderId.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

static string BuildSummary(ExecutionOrder order, BinanceOrderStatusSnapshot snapshot, ExchangeStateDriftStatus status)
{
    var exchangeState = MapExchangeStatus(snapshot.Status);
    return status == ExchangeStateDriftStatus.DriftDetected
        ? $"LocalState={order.State}; ExchangeState={exchangeState}; LocalFilledQuantity={FormatDecimal(order.FilledQuantity)}; ExchangeFilledQuantity={FormatDecimal(snapshot.ExecutedQuantity)}; Source={snapshot.Source}"
        : $"LocalState={order.State}; ExchangeState={exchangeState}; FilledQuantity={FormatDecimal(snapshot.ExecutedQuantity)}; Source={snapshot.Source}";
}

var userId = RequiredEnv("PHASE10I_USER_ID");
var botId = Guid.Parse(RequiredEnv("PHASE10I_BOT_ID"));
var requestedOrderIdRaw = Environment.GetEnvironmentVariable("PHASE10I_ORDER_ID");
var hasRequestedOrderId = Guid.TryParse(requestedOrderIdRaw, out var requestedOrderId) && requestedOrderId != Guid.Empty;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRouting();
builder.Services.AddInfrastructure(builder.Configuration);

using var host = builder.Build();
using var scope = host.Services.CreateScope();

var dataScopeAccessor = scope.ServiceProvider.GetRequiredService<IDataScopeContextAccessor>();
using var dataScope = dataScopeAccessor.BeginScope(userId, hasIsolationBypass: true);

var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
var orderQuery = dbContext.ExecutionOrders
    .IgnoreQueryFilters()
    .Where(entity =>
        entity.OwnerUserId == userId &&
        entity.BotId == botId &&
        entity.ExecutionEnvironment == ExecutionEnvironment.Live &&
        entity.ExecutorKind == ExecutionOrderExecutorKind.Binance &&
        entity.ExchangeAccountId.HasValue &&
        entity.SubmittedToBroker &&
        !entity.IsDeleted);

if (hasRequestedOrderId)
{
    orderQuery = orderQuery.Where(entity => entity.Id == requestedOrderId);
}

var order = await orderQuery
    .OrderByDescending(entity => entity.CreatedDate)
    .FirstOrDefaultAsync()
    ?? throw new InvalidOperationException("No submitted Live/Binance order was found for phase10i reconciliation.");

var credentialService = scope.ServiceProvider.GetRequiredService<IExchangeCredentialService>();
var credentialAccess = await credentialService.GetAsync(
    new ExchangeCredentialAccessRequest(
        order.ExchangeAccountId!.Value,
        "system:phase10i-reconciliation",
        ExchangeCredentialAccessPurpose.Synchronization,
        order.RootCorrelationId),
    CancellationToken.None);

var restClient = scope.ServiceProvider.GetRequiredService<IBinancePrivateRestClient>();
var snapshot = await restClient.GetOrderAsync(
    new BinanceOrderQueryRequest(
        order.ExchangeAccountId.Value,
        order.Symbol,
        order.ExternalOrderId,
        string.IsNullOrWhiteSpace(order.ExternalOrderId) ? BuildDevelopmentFuturesPilotClientOrderId(order.Id) : null,
        credentialAccess.ApiKey,
        credentialAccess.ApiSecret),
    CancellationToken.None);

var exchangeState = MapExchangeStatus(snapshot.Status);
var exchangeFilledQuantity = Math.Min(order.Quantity, snapshot.ExecutedQuantity);
var status = order.State != exchangeState || order.FilledQuantity != exchangeFilledQuantity
    ? ExchangeStateDriftStatus.DriftDetected
    : ExchangeStateDriftStatus.InSync;

var lifecycleService = scope.ServiceProvider.GetRequiredService<ExecutionOrderLifecycleService>();
await lifecycleService.ApplyReconciliationAsync(
    order.Id,
    snapshot,
    status,
    BuildSummary(order, snapshot, status),
    CancellationToken.None);

var refreshedOrder = await dbContext.ExecutionOrders
    .IgnoreQueryFilters()
    .AsNoTracking()
    .SingleAsync(entity => entity.Id == order.Id);
var latestTransition = await dbContext.ExecutionOrderTransitions
    .IgnoreQueryFilters()
    .AsNoTracking()
    .Where(entity => entity.ExecutionOrderId == order.Id && !entity.IsDeleted)
    .OrderByDescending(entity => entity.SequenceNumber)
    .FirstOrDefaultAsync();
var bot = await dbContext.TradingBots
    .IgnoreQueryFilters()
    .AsNoTracking()
    .SingleOrDefaultAsync(entity => entity.Id == botId && !entity.IsDeleted);

Console.WriteLine(JsonSerializer.Serialize(new
{
    Result = "Reconciled",
    OrderId = refreshedOrder.Id,
    Environment = refreshedOrder.ExecutionEnvironment.ToString(),
    ExecutorKind = refreshedOrder.ExecutorKind.ToString(),
    State = refreshedOrder.State.ToString(),
    FailureCode = refreshedOrder.FailureCode,
    LatestTransition = latestTransition?.EventCode,
    ReconciliationStatus = refreshedOrder.ReconciliationStatus.ToString(),
    BotOpenPositionCount = bot?.OpenPositionCount,
    BotOpenOrderCount = bot?.OpenOrderCount
}));
'@

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
    BotExecutionPilot__ExecutionDispatchMode = "Live"
    BotExecutionPilot__SignalEvaluationMode = "Live"
    PHASE10I_USER_ID = $UserId
    PHASE10I_BOT_ID = $BotId.ToString()
    PHASE10I_ORDER_ID = $(if ($OrderId -eq [guid]::Empty) { "" } else { $OrderId.ToString() })
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
Write-Output $safeOutput
