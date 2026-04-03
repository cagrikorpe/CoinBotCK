namespace CoinBot.Infrastructure.Execution;

public sealed class ExecutionValidationException : InvalidOperationException
{
    public ExecutionValidationException(string message)
        : this(nameof(ExecutionValidationException), message)
    {
    }

    public ExecutionValidationException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
            ? nameof(ExecutionValidationException)
            : reasonCode.Trim();
    }

    public string ReasonCode { get; }
}
