using CoinBot.Infrastructure.Execution;

namespace CoinBot.UnitTests.Infrastructure.Execution;

public sealed class ExecutionClientOrderIdTests
{
    [Fact]
    public void CreateDevelopmentFuturesPilot_UsesPilotPrefix_AndRoundTrips()
    {
        var orderId = Guid.NewGuid();

        var clientOrderId = ExecutionClientOrderId.CreateDevelopmentFuturesPilot(orderId);

        Assert.StartsWith("cbp0_", clientOrderId, StringComparison.Ordinal);
        Assert.True(ExecutionClientOrderId.TryParse(clientOrderId, out var parsedOrderId));
        Assert.Equal(orderId, parsedOrderId);
    }
}
