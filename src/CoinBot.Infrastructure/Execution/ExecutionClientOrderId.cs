namespace CoinBot.Infrastructure.Execution;

internal static class ExecutionClientOrderId
{
    private const string DefaultPrefix = "cb_";
    private const string DevelopmentFuturesPilotPrefix = "cbp0_";

    public static string Create(Guid orderId)
    {
        return Create(orderId, DefaultPrefix);
    }

    public static string CreateDevelopmentFuturesPilot(Guid orderId)
    {
        return Create(orderId, DevelopmentFuturesPilotPrefix);
    }

    public static bool TryParse(string? value, out Guid orderId)
    {
        orderId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalizedValue = value.Trim();
        var encodedValue = TryStripKnownPrefix(normalizedValue);

        if (encodedValue is null)
        {
            return false;
        }

        if (Guid.TryParseExact(encodedValue, "N", out orderId))
        {
            return true;
        }

        try
        {
            var paddedValue = encodedValue.Replace('-', '+').Replace('_', '/');

            if (paddedValue.Length % 4 != 0)
            {
                paddedValue = paddedValue.PadRight(paddedValue.Length + (4 - (paddedValue.Length % 4)), '=');
            }

            var bytes = Convert.FromBase64String(paddedValue);

            if (bytes.Length != 16)
            {
                return false;
            }

            orderId = new Guid(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Create(Guid orderId, string prefix)
    {
        return $"{prefix}{Convert.ToBase64String(orderId.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
    }

    private static string? TryStripKnownPrefix(string value)
    {
        foreach (var prefix in new[] { DefaultPrefix, DevelopmentFuturesPilotPrefix })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value[prefix.Length..];
            }
        }

        return null;
    }
}
