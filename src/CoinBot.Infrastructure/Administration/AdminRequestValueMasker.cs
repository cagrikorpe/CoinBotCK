using System.Security.Cryptography;
using System.Text;

namespace CoinBot.Infrastructure.Administration;

public static class AdminRequestValueMasker
{
    public static string? MaskIpAddress(string? ipAddress)
    {
        return Mask(ipAddress, "ip");
    }

    public static string? MaskUserAgent(string? userAgent)
    {
        return Mask(userAgent, "ua");
    }

    private static string? Mask(string? value, string prefix)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedValue));
        return $"{prefix}:{Convert.ToHexStringLower(hash)[..16]}";
    }
}
