using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace CoinBot.UnitTests.Infrastructure.Configuration;

public sealed class DevelopmentConfigurationAlignmentTests
{
    [Fact]
    public void WebAndWorkerDevelopmentDataLatencyGuardThresholds_AreAligned()
    {
        var repoRoot = ResolveRepoRoot();
        var webThresholds = LoadThresholds(Path.Combine(repoRoot, "src", "CoinBot.Web", "appsettings.Development.json"));
        var workerThresholds = LoadThresholds(Path.Combine(repoRoot, "src", "CoinBot.Worker", "appsettings.Development.json"));

        Assert.Equal(workerThresholds.StaleDataThresholdSeconds, webThresholds.StaleDataThresholdSeconds);
        Assert.Equal(workerThresholds.StopDataThresholdSeconds, webThresholds.StopDataThresholdSeconds);
        Assert.Equal(workerThresholds.ClockDriftThresholdSeconds, webThresholds.ClockDriftThresholdSeconds);
    }


    [Fact]
    public void WebAndWorkerProductionDataLatencyGuardThresholds_AreAligned()
    {
        var repoRoot = ResolveRepoRoot();
        var webThresholds = LoadThresholds(Path.Combine(repoRoot, "src", "CoinBot.Web", "appsettings.json"));
        var workerThresholds = LoadThresholds(Path.Combine(repoRoot, "src", "CoinBot.Worker", "appsettings.json"));

        Assert.Equal(workerThresholds.StaleDataThresholdSeconds, webThresholds.StaleDataThresholdSeconds);
        Assert.Equal(workerThresholds.StopDataThresholdSeconds, webThresholds.StopDataThresholdSeconds);
        Assert.Equal(workerThresholds.ClockDriftThresholdSeconds, webThresholds.ClockDriftThresholdSeconds);
    }

    [Fact]
    public void WebAndWorkerEffectiveDevelopmentPilotSymbolScopes_AreAlignedForMultiSymbolTestnet()
    {
        var repoRoot = ResolveRepoRoot();
        var webConfig = LoadEffectiveDevelopmentConfiguration(Path.Combine(repoRoot, "src", "CoinBot.Web"));
        var workerConfig = LoadEffectiveDevelopmentConfiguration(Path.Combine(repoRoot, "src", "CoinBot.Worker"));

        var expectedSymbols = new[] { "BTCUSDT", "ETHUSDT", "SOLUSDT" };

        Assert.Equal(expectedSymbols, LoadStringArray(webConfig, "MarketData:Binance:SeedSymbols"));
        Assert.Equal(expectedSymbols, LoadStringArray(workerConfig, "MarketData:Binance:SeedSymbols"));
        Assert.Equal(expectedSymbols, LoadStringArray(workerConfig, "MarketData:HistoricalGapFiller:Symbols"));
        Assert.Equal(expectedSymbols, LoadStringArray(webConfig, "BotExecutionPilot:AllowedSymbols"));
        Assert.Equal(expectedSymbols, LoadStringArray(workerConfig, "BotExecutionPilot:AllowedSymbols"));
        Assert.Equal(expectedSymbols, LoadStringArray(webConfig, "BotExecutionPilot:AllowedExecutionSymbols"));
        Assert.Equal(expectedSymbols, LoadStringArray(workerConfig, "BotExecutionPilot:AllowedExecutionSymbols"));
    }

    [Fact]
    public void WebAndWorkerEffectiveDevelopmentRuntimeReadiness_StaysTestnetAndHandoffEnabled()
    {
        var repoRoot = ResolveRepoRoot();
        var webConfig = LoadEffectiveDevelopmentConfiguration(Path.Combine(repoRoot, "src", "CoinBot.Web"));
        var workerConfig = LoadEffectiveDevelopmentConfiguration(Path.Combine(repoRoot, "src", "CoinBot.Worker"));

        Assert.True(webConfig.GetValue<bool>("MarketData:Scanner:HandoffEnabled"));
        Assert.True(workerConfig.GetValue<bool>("MarketData:Scanner:HandoffEnabled"));

        Assert.Equal("BinanceTestnet", webConfig["BotExecutionPilot:ExecutionDispatchMode"]);
        Assert.Equal("BinanceTestnet", workerConfig["BotExecutionPilot:ExecutionDispatchMode"]);

        Assert.DoesNotContain("live", webConfig["MarketData:Binance:RestBaseUrl"] ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prod", webConfig["MarketData:Binance:RestBaseUrl"] ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("live", workerConfig["MarketData:Binance:RestBaseUrl"] ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prod", workerConfig["MarketData:Binance:RestBaseUrl"] ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRepoRoot()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);

        while (currentDirectory is not null)
        {
            if (File.Exists(Path.Combine(currentDirectory.FullName, "CoinBot.sln")))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new DirectoryNotFoundException("CoinBot.sln could not be located from the test base directory.");
    }

    private static DataLatencyGuardThresholds LoadThresholds(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var guard = root
            .GetProperty("ExecutionSafety")
            .GetProperty("DataLatencyGuard");

        return new DataLatencyGuardThresholds(
            guard.GetProperty("StaleDataThresholdSeconds").GetInt32(),
            guard.GetProperty("StopDataThresholdSeconds").GetInt32(),
            guard.GetProperty("ClockDriftThresholdSeconds").GetInt32());
    }

    private readonly record struct DataLatencyGuardThresholds(
        int StaleDataThresholdSeconds,
        int StopDataThresholdSeconds,
        int ClockDriftThresholdSeconds);

    private static IConfigurationRoot LoadEffectiveDevelopmentConfiguration(string projectDirectory)
    {
        return new ConfigurationBuilder()
            .SetBasePath(projectDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .Build();
    }

    private static string[] LoadStringArray(IConfiguration configuration, string key)
    {
        return configuration
            .GetSection(key)
            .GetChildren()
            .Select(section => section.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
