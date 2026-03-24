namespace CoinBot.Infrastructure.Execution;

internal static class ExecutionClientOrderId
{
    private const string Prefix = "cb_";

    public static string Create(Guid orderId)
    {
        return $"{Prefix}{Convert.ToBase64String(orderId.ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
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

        var encodedValue = normalizedValue[Prefix.Length..];

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
}
