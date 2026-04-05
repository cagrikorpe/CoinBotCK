using System.Text;
using System.Text.Json;
using CoinBot.Infrastructure.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoinBot.UnitTests.Infrastructure.Observability;

public sealed class HealthCheckResponseWriterTests
{
    [Fact]
    public async Task WriteAsync_OmitsCorrelationIdentifiersAndEntryData()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal)
            {
                ["ready"] = new(
                    HealthStatus.Healthy,
                    "Ready",
                    TimeSpan.FromMilliseconds(12),
                    exception: null,
                    data: new Dictionary<string, object>
                    {
                        ["correlationId"] = "corr-123",
                        ["traceId"] = "trace-123",
                        ["secret"] = "should-not-appear"
                    })
            },
            TimeSpan.FromMilliseconds(12));

        await HealthCheckResponseWriter.WriteAsync(context, report);
        body.Position = 0;
        using var document = await JsonDocument.ParseAsync(body);
        var payload = Encoding.UTF8.GetString(body.ToArray());
        var check = document.RootElement.GetProperty("checks").EnumerateArray().Single();

        Assert.Equal("Healthy", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("ready", check.GetProperty("name").GetString());
        Assert.False(document.RootElement.TryGetProperty("correlationId", out _));
        Assert.False(document.RootElement.TryGetProperty("requestId", out _));
        Assert.False(document.RootElement.TryGetProperty("traceId", out _));
        Assert.False(check.TryGetProperty("data", out _));
        Assert.DoesNotContain("corr-123", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("trace-123", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("should-not-appear", payload, StringComparison.Ordinal);
    }
}
