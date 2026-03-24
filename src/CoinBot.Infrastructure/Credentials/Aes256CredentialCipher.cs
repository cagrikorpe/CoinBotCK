using System.Security.Cryptography;
using System.Text;

namespace CoinBot.Infrastructure.Credentials;

internal sealed class Aes256CredentialCipher(ICredentialKeyResolver keyResolver) : ICredentialCipher
{
    private const byte PayloadVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public string KeyVersion => keyResolver.CurrentKeyVersion;

    public int BlobVersion => PayloadVersion;

    public string Encrypt(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var keyBytes = keyResolver.ResolveKeyMaterial();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        try
        {
            using var aesGcm = new AesGcm(keyBytes, TagSize);
            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

            var payload = new byte[1 + NonceSize + TagSize + ciphertext.Length];
            payload[0] = PayloadVersion;

            Buffer.BlockCopy(nonce, 0, payload, 1, NonceSize);
            Buffer.BlockCopy(tag, 0, payload, 1 + NonceSize, TagSize);
            Buffer.BlockCopy(ciphertext, 0, payload, 1 + NonceSize + TagSize, ciphertext.Length);

            var encodedPayload = Convert.ToBase64String(payload);
            CryptographicOperations.ZeroMemory(payload);
            return encodedPayload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    public string Decrypt(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);

        byte[]? payload = null;
        var keyBytes = keyResolver.ResolveKeyMaterial();

        try
        {
            payload = Convert.FromBase64String(protectedValue);

            if (payload.Length < 1 + NonceSize + TagSize || payload[0] != PayloadVersion)
            {
                throw new InvalidOperationException("Exchange credential payload format is not valid.");
            }

            var nonce = payload.AsSpan(1, NonceSize);
            var tag = payload.AsSpan(1 + NonceSize, TagSize);
            var ciphertext = payload.AsSpan(1 + NonceSize + TagSize);
            var plaintextBytes = new byte[ciphertext.Length];

            try
            {
                using var aesGcm = new AesGcm(keyBytes, TagSize);
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes);
                return Encoding.UTF8.GetString(plaintextBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Exchange credential payload format is not valid.", exception);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("Exchange credential decryption failed.", exception);
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }
}
