using System.Collections.Concurrent;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Web.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoinBot.UnitTests.Web;

public sealed class UserOperationsHubBridgeServiceTests
{
    [Fact]
    public async Task BridgeService_ForwardsUserScopedAndGlobalUpdates_ToExpectedSignalRTargets()
    {
        var streamHub = new UserOperationsStreamHub();
        var hubContext = new TestHubContext();
        var service = new UserOperationsHubBridgeService(
            streamHub,
            hubContext,
            NullLogger<UserOperationsHubBridgeService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        var userUpdate = new UserOperationsUpdate(
            "user-ops-1",
            "ExecutionStateChanged",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Submitted",
            null,
            DateTime.UtcNow);
        var globalUpdate = new UserOperationsUpdate(
            "*",
            "CircuitBreakerChanged",
            null,
            null,
            "Cooldown",
            "SocketClosed",
            DateTime.UtcNow);

        streamHub.Publish(userUpdate);
        streamHub.Publish(globalUpdate);

        var scopedInvocation = await hubContext.WaitForUserInvocationAsync("user-ops-1");
        var globalInvocation = await hubContext.WaitForBroadcastInvocationAsync();

        await service.StopAsync(CancellationToken.None);

        var scopedPayload = Assert.IsType<UserOperationsUpdate>(Assert.Single(scopedInvocation.Arguments));
        var globalPayload = Assert.IsType<UserOperationsUpdate>(Assert.Single(globalInvocation.Arguments));
        Assert.Equal("operationsUpdated", scopedInvocation.MethodName);
        Assert.Equal("operationsUpdated", globalInvocation.MethodName);
        Assert.Equal("ExecutionStateChanged", scopedPayload.EventType);
        Assert.Equal("CircuitBreakerChanged", globalPayload.EventType);
        Assert.Equal("user-ops-1", scopedPayload.OwnerUserId);
        Assert.Equal("*", globalPayload.OwnerUserId);
    }

    [Fact]
    public void UserOperationsHub_RequiresAuthorizeAttribute()
    {
        var authorizeAttribute = Assert.Single(typeof(UserOperationsHub).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true));
        Assert.IsType<AuthorizeAttribute>(authorizeAttribute);
    }

    private sealed class TestHubContext : IHubContext<UserOperationsHub>
    {
        private readonly TestClientProxy broadcastProxy = new();
        private readonly ConcurrentDictionary<string, TestClientProxy> userProxies = new(StringComparer.Ordinal);

        public IHubClients Clients => new TestHubClients(broadcastProxy, userProxies);

        public IGroupManager Groups { get; } = new TestGroupManager();

        public async Task<HubInvocation> WaitForBroadcastInvocationAsync()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (broadcastProxy.Invocations.TryPeek(out var invocation))
                {
                    return invocation;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("No broadcast invocation captured.");
        }

        public async Task<HubInvocation> WaitForUserInvocationAsync(string userId)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (userProxies.TryGetValue(userId, out var proxy) && proxy.Invocations.TryPeek(out var invocation))
                {
                    return invocation;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException($"No user invocation captured for {userId}.");
        }
    }

    private sealed class TestHubClients(
        TestClientProxy broadcastProxy,
        ConcurrentDictionary<string, TestClientProxy> userProxies) : IHubClients
    {
        public IClientProxy All => broadcastProxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => broadcastProxy;

        public IClientProxy Client(string connectionId) => broadcastProxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => broadcastProxy;

        public IClientProxy Group(string groupName) => broadcastProxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => broadcastProxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => broadcastProxy;

        public IClientProxy User(string userId) => userProxies.GetOrAdd(userId, static _ => new TestClientProxy());

        public IClientProxy Users(IReadOnlyList<string> userIds) => broadcastProxy;
    }

    private sealed class TestGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestClientProxy : IClientProxy
    {
        public ConcurrentQueue<HubInvocation> Invocations { get; } = [];

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Invocations.Enqueue(new HubInvocation(method, args));
            return Task.CompletedTask;
        }
    }

    private sealed record HubInvocation(string MethodName, object?[] Arguments);
}
