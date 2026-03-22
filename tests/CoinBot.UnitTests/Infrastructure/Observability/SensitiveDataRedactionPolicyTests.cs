using CoinBot.Infrastructure.Observability;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CoinBot.UnitTests.Infrastructure.Observability;

public sealed class SensitiveDataRedactionPolicyTests
{
    [Fact]
    public void RedactionPolicy_MasksSensitiveStructuredProperties()
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .Destructure.With(new SensitiveDataRedactionPolicy())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information(
            "Structured payload {@Payload}",
            new
            {
                ApiKey = "api-key-value",
                Secret = "secret-value",
                Cookie = "cookie-value",
                Visible = "visible-value",
                Nested = new
                {
                    Token = "token-value",
                    Visible = "nested-visible"
                }
            });

        var logEvent = Assert.Single(sink.Events);
        var payload = Assert.IsType<StructureValue>(logEvent.Properties["Payload"]);

        Assert.Equal("***REDACTED***", ReadScalarValue(payload, "ApiKey"));
        Assert.Equal("***REDACTED***", ReadScalarValue(payload, "Secret"));
        Assert.Equal("***REDACTED***", ReadScalarValue(payload, "Cookie"));
        Assert.Equal("visible-value", ReadScalarValue(payload, "Visible"));

        var nested = Assert.IsType<StructureValue>(payload.Properties.Single(property => property.Name == "Nested").Value);
        Assert.Equal("***REDACTED***", ReadScalarValue(nested, "Token"));
        Assert.Equal("nested-visible", ReadScalarValue(nested, "Visible"));
    }

    private static string? ReadScalarValue(StructureValue structureValue, string propertyName)
    {
        return Assert.IsType<ScalarValue>(structureValue.Properties.Single(property => property.Name == propertyName).Value).Value?.ToString();
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
        }
    }
}
