namespace CoinBot.Web;

internal static class WebHostBootstrap
{
    public static WebApplicationOptions CreateOptions(string[] args)
    {
        return new WebApplicationOptions
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
        if (ContainsWebConfiguration(normalizedCurrentDirectory))
        {
            return normalizedCurrentDirectory;
        }

        var normalizedAppBaseDirectory = Path.GetFullPath(appBaseDirectory);
        if (ContainsWebConfiguration(normalizedAppBaseDirectory))
        {
            return normalizedAppBaseDirectory;
        }

        return normalizedCurrentDirectory;
    }

    private static bool ContainsWebConfiguration(string directoryPath)
    {
        return File.Exists(Path.Combine(directoryPath, "appsettings.json"));
    }
}
