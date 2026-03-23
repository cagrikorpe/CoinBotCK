namespace CoinBot.Infrastructure.Execution;

internal static class ExecutionClientOrderId
{
    private const string Prefix = "cb_";

    public static string Create(Guid orderId)
    {
        return $"{Prefix}{orderId:N}";
    }

    public static bool TryParse(string? value, out Guid orderId)
    {
        orderId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalizedValue = value.Trim();

        if (!normalizedValue.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Guid.TryParseExact(normalizedValue[Prefix.Length..], "N", out orderId);
    }
}
