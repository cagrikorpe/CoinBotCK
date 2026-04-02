namespace CoinBot.Infrastructure.Execution;

public sealed class ExecutionValidationException(string message) : InvalidOperationException(message);
