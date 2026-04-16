using System.Text.Json;

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
}
