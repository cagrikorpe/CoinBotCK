using System.Diagnostics;
using CoinBot.Infrastructure.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Infrastructure.Observability;

public sealed class CorrelationContextMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_SetsResponseHeaders_AndFlowsCurrentContextWithinTheRequest()
    {
        var correlationContextAccessor = new CorrelationContextAccessor();
        CorrelationContext? capturedContext = null;
        var middleware = new CorrelationContextMiddleware(
            _ =>
            {
                capturedContext = correlationContextAccessor.Current;
                return Task.CompletedTask;
            },
            correlationContextAccessor,
            NullLogger<CorrelationContextMiddleware>.Instance);
        var httpContext = new DefaultHttpContext();
        using var requestActivity = new Activity("test-request").Start();

        httpContext.Request.Headers[CorrelationHeaderNames.CorrelationId] = "corr-req-001";
        httpContext.Request.Headers[CorrelationHeaderNames.RequestId] = "req-req-001";

        await middleware.InvokeAsync(httpContext);

        Assert.NotNull(capturedContext);
        Assert.Equal("corr-req-001", capturedContext!.CorrelationId);
        Assert.Equal("req-req-001", capturedContext.RequestId);
        Assert.Equal(requestActivity.TraceId.ToString(), capturedContext.TraceId);
        Assert.Equal("corr-req-001", httpContext.Response.Headers[CorrelationHeaderNames.CorrelationId]);
        Assert.Equal("req-req-001", httpContext.Response.Headers[CorrelationHeaderNames.RequestId]);
        Assert.Equal(requestActivity.TraceId.ToString(), httpContext.Response.Headers[CorrelationHeaderNames.TraceId].ToString());
        Assert.Null(correlationContextAccessor.Current);
    }

    [Fact]
    public async Task InvokeAsync_GeneratesSafeIdentifiers_WhenHeadersAreMissingOrInvalid()
    {
        var correlationContextAccessor = new CorrelationContextAccessor();
        CorrelationContext? capturedContext = null;
        var middleware = new CorrelationContextMiddleware(
            _ =>
            {
                capturedContext = correlationContextAccessor.Current;
                return Task.CompletedTask;
            },
            correlationContextAccessor,
            NullLogger<CorrelationContextMiddleware>.Instance);
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = "server-trace-001"
        };

        httpContext.Request.Headers[CorrelationHeaderNames.CorrelationId] = "bad\r\nvalue";
        httpContext.Request.Headers[CorrelationHeaderNames.RequestId] = "bad value";

        await middleware.InvokeAsync(httpContext);

        Assert.NotNull(capturedContext);
        Assert.NotEqual("bad\r\nvalue", capturedContext!.CorrelationId);
        Assert.Equal("server-trace-001", capturedContext.RequestId);
        Assert.False(string.IsNullOrWhiteSpace(capturedContext.TraceId));
        Assert.Equal(capturedContext.CorrelationId, httpContext.Response.Headers[CorrelationHeaderNames.CorrelationId].ToString());
        Assert.Equal("server-trace-001", httpContext.Response.Headers[CorrelationHeaderNames.RequestId].ToString());
        Assert.Equal(capturedContext.TraceId, httpContext.Response.Headers[CorrelationHeaderNames.TraceId].ToString());
    }
}
