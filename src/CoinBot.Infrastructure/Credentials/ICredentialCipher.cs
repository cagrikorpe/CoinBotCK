namespace CoinBot.Infrastructure.Credentials;

public interface ICredentialCipher
{
    string KeyVersion { get; }

    int BlobVersion { get; }

    string Encrypt(string plaintext);

    string Decrypt(string protectedValue);
}
