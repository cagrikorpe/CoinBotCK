using CoinBot.Web;

namespace CoinBot.UnitTests.Web;

public sealed class WebHostBootstrapTests
{
    [Fact]
    public void ResolveContentRootPath_UsesAppBaseDirectory_WhenCurrentDirectoryDoesNotContainWebConfig()
    {
        var currentDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var appBaseDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(appBaseDirectory);
        File.WriteAllText(Path.Combine(appBaseDirectory, "appsettings.json"), "{}");

        try
        {
            var resolvedPath = WebHostBootstrap.ResolveContentRootPath(currentDirectory, appBaseDirectory);
            Assert.Equal(Path.GetFullPath(appBaseDirectory), resolvedPath);
        }
        finally
        {
            Directory.Delete(currentDirectory, recursive: true);
            Directory.Delete(appBaseDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveContentRootPath_PrefersCurrentDirectory_WhenWebConfigExists()
    {
        var currentDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var appBaseDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(appBaseDirectory);
        File.WriteAllText(Path.Combine(currentDirectory, "appsettings.json"), "{}");
        File.WriteAllText(Path.Combine(appBaseDirectory, "appsettings.json"), "{}");

        try
        {
            var resolvedPath = WebHostBootstrap.ResolveContentRootPath(currentDirectory, appBaseDirectory);
            Assert.Equal(Path.GetFullPath(currentDirectory), resolvedPath);
        }
        finally
        {
            Directory.Delete(currentDirectory, recursive: true);
            Directory.Delete(appBaseDirectory, recursive: true);
        }
    }
}
