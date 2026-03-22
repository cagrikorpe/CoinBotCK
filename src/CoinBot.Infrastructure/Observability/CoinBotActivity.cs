using System.Diagnostics;

namespace CoinBot.Infrastructure.Observability;

public static class CoinBotActivity
{
    public const string SourceName = "CoinBot";

    private static readonly ActivitySource Source = new(SourceName);

    public static Activity StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return Source.StartActivity(name, kind) ?? new Activity(name).Start();
    }
}
