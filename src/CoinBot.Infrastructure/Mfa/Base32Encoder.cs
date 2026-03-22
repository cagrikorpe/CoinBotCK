using System.Text;

namespace CoinBot.Infrastructure.Mfa;

internal static class Base32Encoder
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var currentByte in bytes)
        {
            buffer = (buffer << 8) | currentByte;
            bitsLeft += 8;

            while (bitsLeft >= 5)
            {
                output.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 0x1F]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }

        return output.ToString();
    }

    public static byte[] Decode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .TrimEnd('=')
            .ToUpperInvariant();

        if (normalized.Length == 0)
        {
            return [];
        }

        var output = new List<byte>((normalized.Length * 5) / 8);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var currentChar in normalized)
        {
            var index = Alphabet.IndexOf(currentChar);

            if (index < 0)
            {
                throw new FormatException("The provided secret is not a valid Base32 string.");
            }

            buffer = (buffer << 5) | index;
            bitsLeft += 5;

            if (bitsLeft < 8)
            {
                continue;
            }

            output.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
            bitsLeft -= 8;
        }

        return [.. output];
    }
}
