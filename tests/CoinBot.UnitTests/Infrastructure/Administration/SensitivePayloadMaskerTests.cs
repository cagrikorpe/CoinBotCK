using CoinBot.Infrastructure.Administration;

namespace CoinBot.UnitTests.Infrastructure.Administration;

public sealed class SensitivePayloadMaskerTests
{
    [Fact]
    public void Mask_RedactsSensitiveJsonFields()
    {
        var masked = SensitivePayloadMasker.Mask(
            """
            {
              "apiKey": "plain-key",
              "secret": "plain-secret",
              "authorization": "Bearer plain-token",
              "payload": {
                "signature": "abc123",
                "symbol": "BTCUSDT"
              }
            }
            """);

        Assert.NotNull(masked);
        Assert.DoesNotContain("plain-key", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-secret", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-token", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", masked, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", masked, StringComparison.Ordinal);
        Assert.Contains("BTCUSDT", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_RedactsSensitiveQueryStringFields()
    {
        var masked = SensitivePayloadMasker.Mask(
            "/fapi/v1/order?symbol=BTCUSDT&signature=abc123&timestamp=123&apiKey=plain-key");

        Assert.NotNull(masked);
        Assert.DoesNotContain("abc123", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-key", masked, StringComparison.Ordinal);
        Assert.Contains("symbol=BTCUSDT", masked, StringComparison.Ordinal);
        Assert.Contains("signature=***REDACTED***", masked, StringComparison.Ordinal);
        Assert.Contains("apiKey=***REDACTED***", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_RedactsSensitiveJsonStringValues()
    {
        var masked = SensitivePayloadMasker.Mask(
            """
            {
              "endpoint": "/fapi/v1/order?symbol=BTCUSDT&signature=abc123&apiKey=plain-key",
              "nested": {
                "url": "/fapi/v3/account?timestamp=123&signature=abc123"
              }
            }
            """);

        Assert.NotNull(masked);
        Assert.DoesNotContain("plain-key", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", masked, StringComparison.Ordinal);
        Assert.Contains("signature=***REDACTED***", masked, StringComparison.Ordinal);
        Assert.Contains("apiKey=***REDACTED***", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_RedactsCookieRefreshTokenCsrfAndConnectionStringCredentials()
    {
        var masked = SensitivePayloadMasker.Mask(
            """
            {
              "cookie": "session-cookie",
              "refreshToken": "plain-refresh-token",
              "csrfToken": "plain-csrf-token",
              "connectionString": "Server=localhost;User Id=local-user;Password=plain-db-password;TrustServerCertificate=true;"
            }
            """);

        Assert.NotNull(masked);
        Assert.DoesNotContain("session-cookie", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-refresh-token", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-csrf-token", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-db-password", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("local-user", masked, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", masked, StringComparison.Ordinal);
    }
}
