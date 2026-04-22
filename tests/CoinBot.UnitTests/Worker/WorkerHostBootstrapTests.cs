using CoinBot.Worker;

namespace CoinBot.UnitTests.Worker;

public sealed class WorkerHostBootstrapTests
{
    [Fact]
    public void ResolveContentRootPath_UsesAppBaseDirectory_WhenCurrentDirectoryDoesNotContainWorkerConfig()
    {
        var currentDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var appBaseDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(appBaseDirectory);
        File.WriteAllText(Path.Combine(appBaseDirectory, "appsettings.json"), "{}");

        try
        {
            var resolvedPath = WorkerHostBootstrap.ResolveContentRootPath(currentDirectory, appBaseDirectory);

            Assert.Equal(Path.GetFullPath(appBaseDirectory), resolvedPath);
        }
        finally
        {
            Directory.Delete(currentDirectory, recursive: true);
            Directory.Delete(appBaseDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveContentRootPath_PrefersCurrentDirectory_WhenWorkerConfigExists()
    {
        var currentDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var appBaseDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(appBaseDirectory);
        File.WriteAllText(Path.Combine(currentDirectory, "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(appBaseDirectory, "appsettings.json"), "{}");

        try
        {
            var resolvedPath = WorkerHostBootstrap.ResolveContentRootPath(currentDirectory, appBaseDirectory);

            Assert.Equal(Path.GetFullPath(currentDirectory), resolvedPath);
        }
        finally
        {
            Directory.Delete(currentDirectory, recursive: true);
            Directory.Delete(appBaseDirectory, recursive: true);
        }
    }
}
