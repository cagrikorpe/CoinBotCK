using System.Security.Claims;
using System.Text;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Infrastructure.Administration;
using CoinBot.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace CoinBot.UnitTests.Web;

public sealed class LogCenterControllerTests
{
    [Fact]
    public async Task Index_PassesFiltersAndFocusToReadModel()
    {
        var now = new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);
        var readModel = new FakeLogCenterReadModelService(
            new LogCenterPageSnapshot(
                new LogCenterQueryRequest(null, null, null, null, null, null, null, null, null, 100),
                new LogCenterSummarySnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, now),
                new LogCenterRetentionSnapshot(true, 45, 45, 90, 180, 180, 250, now, "Retention completed"),
                Array.Empty<LogCenterEntrySnapshot>(),
                false,
                null));
        var controller = CreateController(readModel, new FakeAdminAuditLogService());

        var result = await controller.Index(
            query: "corr-hit",
            correlationId: "corr-hit",
            decisionId: "dec-hit",
            executionAttemptId: "exe-hit",
            userId: "user-hit",
            symbol: "BTCUSDT",
            status: "critical",
            fromUtc: now.AddDays(-1),
            toUtc: now,
            take: 25,
            focus: "focus-hit",
            cancellationToken: CancellationToken.None);

        var viewResult = Assert.IsType<ViewResult>(result);
        Assert.Same(readModel.Snapshot, viewResult.Model);
        Assert.Equal("focus-hit", controller.ViewData["LogCenterFocusReference"]);
        Assert.Equal(25, controller.ViewData["LogCenterExportTake"]);

        var request = readModel.CapturedRequest;
        Assert.NotNull(request);
        Assert.Equal("corr-hit", request.Query);
        Assert.Equal("corr-hit", request.CorrelationId);
        Assert.Equal("dec-hit", request.DecisionId);
        Assert.Equal("exe-hit", request.ExecutionAttemptId);
        Assert.Equal("user-hit", request.UserId);
        Assert.Equal("BTCUSDT", request.Symbol);
        Assert.Equal("critical", request.Status);
        Assert.Equal(25, request.Take);
    }

    [Fact]
    public async Task Export_ReturnsMaskedCsv_AndWritesAuditEntry()
    {
        var now = new DateTime(2026, 3, 24, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new LogCenterPageSnapshot(
            new LogCenterQueryRequest("api", null, null, null, null, null, null, null, null, 100),
            new LogCenterSummarySnapshot(1, 1, 0, 0, 0, 0, 0, 0, 0, now),
            new LogCenterRetentionSnapshot(true, 45, 45, 90, 180, 180, 250, now, "Retention completed"),
            [
                new LogCenterEntrySnapshot(
                    "DecisionTrace",
                    "dec-hit",
                    "Persisted",
                    "critical",
                    "Critical",
                    "corr-hit",
                    "dec-hit",
                    null,
                    null,
                    null,
                    "user-hit",
                    "BTCUSDT",
                    "apiSecret=plain-secret title",
                    "signature=abc123; apiKey=plain-key; token=plain-token",
                    "Authorization: Bearer plain-token",
                    now,
                    ["apiKey=plain-key", "signature=abc123", "secret=plain-secret"],
                    "{\"apiSecret\":\"plain-secret\"}")
            ],
            false,
            null);
        var readModel = new FakeLogCenterReadModelService(snapshot);
        var auditLog = new FakeAdminAuditLogService();
        var controller = CreateController(readModel, auditLog);

        var result = await controller.Export(
            query: "api",
            correlationId: "corr-hit",
            decisionId: "dec-hit",
            executionAttemptId: null,
            userId: "user-hit",
            symbol: "BTCUSDT",
            status: null,
            fromUtc: null,
            toUtc: null,
            take: 100,
            cancellationToken: CancellationToken.None);

        var fileResult = Assert.IsType<FileContentResult>(result);
        var csv = Encoding.UTF8.GetString(fileResult.FileContents);

        Assert.Contains("Kind,Reference,Status,Tone,Severity", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-secret", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-key", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-token", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", csv, StringComparison.Ordinal);
        Assert.Contains("***REDACTED***", csv, StringComparison.Ordinal);
        Assert.Single(auditLog.Requests);
        Assert.Equal("LogCenter.Export", auditLog.Requests[0].ActionType);
        Assert.Equal("LogCenter", auditLog.Requests[0].TargetType);
        Assert.Equal("Masked log export", auditLog.Requests[0].Reason);
    }

    private static LogCenterController CreateController(
        FakeLogCenterReadModelService readModelService,
        FakeAdminAuditLogService auditLogService)
    {
        var httpContext = new DefaultHttpContext
        {
            TraceIdentifier = "trace-log-center-1",
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "admin-01")
                ],
                "TestAuth"))
        };

        var controller = new LogCenterController(readModelService, auditLogService, TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider())
        };

        return controller;
    }

    private sealed class FakeLogCenterReadModelService(LogCenterPageSnapshot snapshot) : ILogCenterReadModelService
    {
        public LogCenterPageSnapshot Snapshot { get; } = snapshot;

        public LogCenterQueryRequest? CapturedRequest { get; private set; }

        public Task<LogCenterPageSnapshot> GetPageAsync(LogCenterQueryRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class FakeAdminAuditLogService : IAdminAuditLogService
    {
        public List<AdminAuditLogWriteRequest> Requests { get; } = [];

        public Task WriteAsync(AdminAuditLogWriteRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>(StringComparer.Ordinal);

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
