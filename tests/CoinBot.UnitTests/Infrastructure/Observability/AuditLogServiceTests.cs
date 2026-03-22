using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.UnitTests.Infrastructure.Observability;

public sealed class AuditLogServiceTests
{
    [Fact]
    public async Task WriteAsync_UsesCurrentCorrelationContext_WhenRequestDoesNotProvideCorrelationId()
    {
        await using var dbContext = CreateDbContext();
        var correlationContextAccessor = new CorrelationContextAccessor();
        using var _ = correlationContextAccessor.BeginScope(
            new CorrelationContext("corr-ctx-001", "req-ctx-001", "trace-ctx-001", "span-ctx-001"));

        var auditLogService = new AuditLogService(dbContext, correlationContextAccessor);

        await auditLogService.WriteAsync(
            new AuditLogWriteRequest(
                "system",
                "ExecutionGate.Allowed",
                "TradeExecution/Dispatch",
                "Test context",
                null,
                "Allowed",
                "Demo"));

        var auditLog = await dbContext.AuditLogs.SingleAsync();

        Assert.Equal("corr-ctx-001", auditLog.CorrelationId);
    }

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new ApplicationDbContext(options, new TestDataScopeContext());
    }

    private sealed class TestDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
