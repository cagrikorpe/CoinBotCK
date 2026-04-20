[CmdletBinding()]
param(
    [string]$Server = "localhost",
    [string]$Database = "CoinBotDb",
    [string]$UserId = "5a8675cf-cbcb-4c28-82fb-304aee442895",
    [guid]$BotId = "8EA3ED8B-C61F-4A44-A683-168AE9C69C43",
    [string]$Symbol = "SOLUSDT",
    [string]$FuturesRestBaseUrl = "https://testnet.binancefuture.com",
    [string]$FuturesWebSocketBaseUrl = "wss://fstream.binancefuture.com",
    [string]$EvidencePath = "",
    [string]$RunnerRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($RunnerRoot)) {
    $RunnerRoot = Join-Path $repoRoot "artifacts\phase10k-position-truth-runner"
}
if ([string]::IsNullOrWhiteSpace($EvidencePath)) {
    $EvidencePath = Join-Path $repoRoot "artifacts\phase10k-position-truth.json"
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
    throw "Testnet-only endpoint guard rejected the configured endpoint. Position truth refresh was not started."
}

New-Item -ItemType Directory -Path $RunnerRoot -Force | Out-Null
$evidenceDirectory = Split-Path $EvidencePath -Parent
if (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) {
    New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
}

$runnerProject = Join-Path $RunnerRoot "Phase10kPositionTruthRunner.csproj"
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
using System.Reflection;
using System.Text.Json;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.Exchange;
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

static async Task<int> RunExecutionReconciliationAsync(IServiceProvider services)
{
    var reconciliationService = services.GetRequiredService<ExecutionReconciliationService>();
    var method = typeof(ExecutionReconciliationService).GetMethod(
        "RunOnceAsync",
        BindingFlags.Instance | BindingFlags.NonPublic);
    if (method is null)
    {
        throw new InvalidOperationException("Execution reconciliation entry point could not be resolved.");
    }

    var task = (Task<int>?)method.Invoke(reconciliationService, [CancellationToken.None]);
    if (task is null)
    {
        throw new InvalidOperationException("Execution reconciliation did not return a task.");
    }

    return await task;
}

var userId = RequiredEnv("PHASE10K_USER_ID");
var botId = Guid.Parse(RequiredEnv("PHASE10K_BOT_ID"));
var symbol = RequiredEnv("PHASE10K_SYMBOL").ToUpperInvariant();
var evidencePath = RequiredEnv("PHASE10K_EVIDENCE_PATH");

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRouting();
builder.Services.AddInfrastructure(builder.Configuration);

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var services = scope.ServiceProvider;
var dataScopeAccessor = services.GetRequiredService<IDataScopeContextAccessor>();
using var dataScope = dataScopeAccessor.BeginScope(userId, hasIsolationBypass: true);
var dbContext = services.GetRequiredService<ApplicationDbContext>();

var bot = await dbContext.TradingBots.IgnoreQueryFilters()
    .SingleAsync(entity => entity.Id == botId && entity.OwnerUserId == userId && !entity.IsDeleted);
if (!bot.ExchangeAccountId.HasValue)
{
    throw new InvalidOperationException("Target bot has no exchange account.");
}

var credentialService = services.GetRequiredService<IExchangeCredentialService>();
var credentialAccess = await credentialService.GetAsync(
    new ExchangeCredentialAccessRequest(
        bot.ExchangeAccountId.Value,
        "system:phase10k-position-truth-refresh",
        ExchangeCredentialAccessPurpose.Synchronization,
        "phase10k-position-truth-" + Guid.NewGuid().ToString("N")));

var restClient = services.GetRequiredService<IBinancePrivateRestClient>();
var balanceSync = services.GetRequiredService<ExchangeBalanceSyncService>();
var positionSync = services.GetRequiredService<ExchangePositionSyncService>();
var syncState = services.GetRequiredService<ExchangeAccountSyncStateService>();

var reconciledOrderCount = await RunExecutionReconciliationAsync(services);
dbContext.ChangeTracker.Clear();

var snapshot = await restClient.GetAccountSnapshotAsync(
    bot.ExchangeAccountId.Value,
    userId,
    "Binance",
    credentialAccess.ApiKey,
    credentialAccess.ApiSecret);

await balanceSync.ApplyAsync(snapshot);
await syncState.RecordBalanceSyncAsync(snapshot);
await positionSync.ApplyAsync(snapshot);
await syncState.RecordPositionSyncAsync(snapshot);
dbContext.ChangeTracker.Clear();

var dbPositions = await dbContext.ExchangePositions.IgnoreQueryFilters().AsNoTracking()
    .Where(entity =>
        entity.OwnerUserId == userId &&
        entity.ExchangeAccountId == bot.ExchangeAccountId.Value &&
        entity.Plane == ExchangeDataPlane.Futures &&
        entity.Symbol == symbol &&
        !entity.IsDeleted &&
        entity.Quantity != 0m)
    .ToListAsync();
var refreshedBot = await dbContext.TradingBots.IgnoreQueryFilters().AsNoTracking()
    .SingleAsync(entity => entity.Id == botId && !entity.IsDeleted);
var latestLiveBinanceOrder = await dbContext.ExecutionOrders.IgnoreQueryFilters().AsNoTracking()
    .Where(entity =>
        entity.OwnerUserId == userId &&
        entity.ExecutionEnvironment == ExecutionEnvironment.Live &&
        entity.ExecutorKind == ExecutionOrderExecutorKind.Binance &&
        !entity.IsDeleted)
    .OrderByDescending(entity => entity.CreatedDate)
    .FirstOrDefaultAsync();

var exchangeNetQuantity = snapshot.Positions
    .Where(position => string.Equals(position.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
    .Sum(SignedSnapshotPositionQuantity);
var dbNetQuantity = dbPositions.Sum(SignedEntityPositionQuantity);

var evidence = new
{
    Result = "PositionTruthRefreshed",
    GeneratedAtUtc = DateTime.UtcNow,
    BotId = refreshedBot.Id,
    refreshedBot.ExchangeAccountId,
    Symbol = symbol,
    latestEnvironment = latestLiveBinanceOrder?.ExecutionEnvironment.ToString() ?? "none",
    latestExecutorKind = latestLiveBinanceOrder?.ExecutorKind.ToString() ?? "none",
    latestOrderId = latestLiveBinanceOrder?.Id,
    latestOrderState = latestLiveBinanceOrder?.State.ToString() ?? "none",
    latestExchangeNetQuantity = FormatDecimal(exchangeNetQuantity),
    latestDbNetQuantity = FormatDecimal(dbNetQuantity),
    latestBotOpenPositionCount = refreshedBot.OpenPositionCount,
    latestBotOpenOrderCount = refreshedBot.OpenOrderCount,
    reconciledOrderCount,
    snapshotReceivedAtUtc = snapshot.ReceivedAtUtc
};

var json = JsonSerializer.Serialize(evidence);
await File.WriteAllTextAsync(evidencePath, json);
Console.WriteLine(json);
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
    BotExecutionPilot__SignalEvaluationMode = "Live"
    BotExecutionPilot__ExecutionDispatchMode = "Live"
    PHASE10K_USER_ID = $UserId
    PHASE10K_BOT_ID = $BotId.ToString()
    PHASE10K_SYMBOL = $Symbol
    PHASE10K_EVIDENCE_PATH = $EvidencePath
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

$runnerOutput | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
