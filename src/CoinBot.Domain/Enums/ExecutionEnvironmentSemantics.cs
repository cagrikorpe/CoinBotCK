namespace CoinBot.Domain.Enums;

public static class ExecutionEnvironmentSemantics
{
    public static bool UsesInternalDemoExecution(
        ExecutionEnvironment environment,
        bool allowInternalDemoExecution)
    {
        return environment == ExecutionEnvironment.Demo &&
               allowInternalDemoExecution;
    }

    public static bool UsesBrokerBackedTestnet(
        ExecutionEnvironment environment,
        bool allowInternalDemoExecution)
    {
        return environment == ExecutionEnvironment.BinanceTestnet ||
               (environment == ExecutionEnvironment.Demo && !allowInternalDemoExecution);
    }

    public static bool IsBrokerBacked(
        ExecutionEnvironment environment,
        bool allowInternalDemoExecution)
    {
        return environment == ExecutionEnvironment.Live ||
               UsesBrokerBackedTestnet(environment, allowInternalDemoExecution);
    }

    public static bool IsLiveLike(ExecutionEnvironment environment)
    {
        return environment is ExecutionEnvironment.Live or ExecutionEnvironment.BinanceTestnet;
    }

    public static string ToOperatorLabel(ExecutionEnvironment environment)
    {
        return environment switch
        {
            ExecutionEnvironment.BinanceTestnet => "Testnet",
            ExecutionEnvironment.Live => "Live",
            _ => "Demo"
        };
    }
}
