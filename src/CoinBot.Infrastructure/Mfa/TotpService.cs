using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Mfa;

public sealed class TotpService : ITotpService
{
    private readonly IDataProtector secretProtector;
    private readonly TimeProvider timeProvider;
    private readonly MfaOptions options;
    private readonly int codeModulo;

    public TotpService(
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider,
        IOptions<MfaOptions> options)
    {
        secretProtector = dataProtectionProvider.CreateProtector("CoinBot.Mfa.TotpSecret.v1");
        this.timeProvider = timeProvider;
        this.options = options.Value;
        codeModulo = CalculateCodeModulo(this.options.TotpCodeLength);
    }

    public string GenerateSecret()
    {
        var secretBytes = RandomNumberGenerator.GetBytes(options.TotpSecretSizeBytes);
        return Base32Encoder.Encode(secretBytes);
    }

    public string ProtectSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var normalizedSecret = NormalizeSecret(secret);
        _ = Base32Encoder.Decode(normalizedSecret);

        return secretProtector.Protect(normalizedSecret);
    }

    public bool VerifyCode(string? protectedSecret, string code)
    {
        var normalizedCode = NormalizeCode(code);

        if (normalizedCode.Length != options.TotpCodeLength)
        {
            return false;
        }

        if (!TryUnprotectSecret(protectedSecret, out var secretBytes))
        {
            return false;
        }

        var currentCounter = timeProvider.GetUtcNow().ToUnixTimeSeconds() / options.TotpTimeStepSeconds;

        for (var offset = -options.TotpAllowedTimeDriftSteps; offset <= options.TotpAllowedTimeDriftSteps; offset++)
        {
            var counter = currentCounter + offset;

            if (counter < 0)
            {
                continue;
            }

            var expectedCode = ComputeTotp(secretBytes, counter);

            if (FixedTimeEquals(normalizedCode, expectedCode))
            {
                return true;
            }
        }

        return false;
    }

    private string ComputeTotp(byte[] secretBytes, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        using var hmac = new HMACSHA1(secretBytes);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0F;
        var binaryCode =
            ((hash[offset] & 0x7F) << 24) |
            (hash[offset + 1] << 16) |
            (hash[offset + 2] << 8) |
            hash[offset + 3];

        var code = binaryCode % codeModulo;
        return code.ToString($"D{options.TotpCodeLength}");
    }

    private bool TryUnprotectSecret(string? protectedSecret, out byte[] secretBytes)
    {
        secretBytes = [];

        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return false;
        }

        try
        {
            var normalizedSecret = secretProtector.Unprotect(protectedSecret);
            secretBytes = Base32Encoder.Decode(normalizedSecret);
            return secretBytes.Length > 0;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string NormalizeSecret(string secret)
    {
        return secret
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }

    private static string NormalizeCode(string code)
    {
        return new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
    }

    private static int CalculateCodeModulo(int digits)
    {
        var modulo = 1;

        for (var index = 0; index < digits; index++)
        {
            modulo *= 10;
        }

        return modulo;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
