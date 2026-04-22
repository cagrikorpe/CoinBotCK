using Microsoft.Extensions.Hosting;

namespace CoinBot.Worker;

internal static class WorkerHostBootstrap
{
    public static HostApplicationBuilderSettings CreateSettings(string[] args)
    {
        return new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = ResolveContentRootPath(
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory)
        };
    }

    internal static string ResolveContentRootPath(string currentDirectory, string appBaseDirectory)
    {
        var normalizedCurrentDirectory = Path.GetFullPath(currentDirectory);
        if (ContainsWorkerConfiguration(normalizedCurrentDirectory))
        {
            return normalizedCurrentDirectory;
        }

        var normalizedAppBaseDirectory = Path.GetFullPath(appBaseDirectory);
        if (ContainsWorkerConfiguration(normalizedAppBaseDirectory))
        {
            return normalizedAppBaseDirectory;
        }

        return normalizedCurrentDirectory;
    }

    private static bool ContainsWorkerConfiguration(string directoryPath)
    {
        return File.Exists(Path.Combine(directoryPath, "appsettings.json"));
    }
}
