using System.Security.Claims;
using System.Text;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Administration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.Web.Controllers;

[Authorize(Policy = ApplicationPolicies.AuditRead)]
public sealed class LogCenterController(
    ILogCenterReadModelService logCenterReadModelService,
    IAdminAuditLogService adminAuditLogService,
    TimeProvider timeProvider) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        string? query,
        string? correlationId,
        string? decisionId,
        string? executionAttemptId,
        string? userId,
        string? symbol,
        string? status,
        DateTime? fromUtc,
        DateTime? toUtc,
        int take = 100,
        string? focus = null,
        CancellationToken cancellationToken = default)
    {
        ViewData["LogCenterFocusReference"] = NormalizeOptional(focus, 128);
        ViewData["LogCenterExportTake"] = NormalizeTake(take, 100);

        var snapshot = await logCenterReadModelService.GetPageAsync(
            new LogCenterQueryRequest(
                query,
                correlationId,
                decisionId,
                executionAttemptId,
                userId,
                symbol,
                status,
                fromUtc,
                toUtc,
                NormalizeTake(take, 100)),
            cancellationToken);

        return View(snapshot);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Export(
        string? query,
        string? correlationId,
        string? decisionId,
        string? executionAttemptId,
        string? userId,
        string? symbol,
        string? status,
        DateTime? fromUtc,
        DateTime? toUtc,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var normalizedTake = NormalizeTake(take, 1000);
        var request = new LogCenterQueryRequest(
            query,
            correlationId,
            decisionId,
            executionAttemptId,
            userId,
            symbol,
            status,
            fromUtc,
            toUtc,
            normalizedTake);

        try
        {
            var snapshot = await logCenterReadModelService.GetPageAsync(request, cancellationToken);
            var csv = BuildCsv(snapshot);

            await adminAuditLogService.WriteAsync(
                new AdminAuditLogWriteRequest(
                    ResolveActorUserId(),
                    "LogCenter.Export",
                    "LogCenter",
                    null,
                    BuildFilterSummary(request),
                    $"Rows={snapshot.Entries.Count}; KindCounts={BuildKindSummary(snapshot.Entries)}",
                    "Masked log export",
                    ResolveMaskedRemoteIpAddress(),
                    ResolveMaskedUserAgent(),
                    HttpContext.TraceIdentifier),
                cancellationToken);

            var bytes = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(csv))
                .ToArray();

            var fileName = $"log-center-{timeProvider.GetUtcNow():yyyyMMdd-HHmmss}.csv";
            return File(bytes, "text/csv; charset=utf-8", fileName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                await adminAuditLogService.WriteAsync(
                    new AdminAuditLogWriteRequest(
                        ResolveActorUserId(),
                        "LogCenter.Export.Failed",
                        "LogCenter",
                        null,
                        BuildFilterSummary(request),
                        null,
                        Truncate($"Export failed: {exception.Message}", 512),
                        ResolveMaskedRemoteIpAddress(),
                        ResolveMaskedUserAgent(),
                        HttpContext.TraceIdentifier),
                    cancellationToken);
            }
            catch (Exception auditException) when (auditException is not OperationCanceledException)
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }

            return Problem("Log export could not be generated.");
        }
    }

    private string ResolveActorUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.Identity?.Name
            ?? "system";
    }

    private string? ResolveMaskedRemoteIpAddress()
    {
        return AdminRequestValueMasker.MaskIpAddress(HttpContext.Connection.RemoteIpAddress?.ToString());
    }

    private string? ResolveMaskedUserAgent()
    {
        return AdminRequestValueMasker.MaskUserAgent(Request.Headers["User-Agent"].ToString());
    }

    private static string BuildCsv(LogCenterPageSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Kind,Reference,Status,Tone,Severity,CorrelationId,DecisionId,ExecutionAttemptId,IncidentReference,ApprovalReference,UserId,Symbol,Title,Summary,Source,CreatedAtUtc,Tags");

        foreach (var entry in snapshot.Entries)
        {
            builder.AppendLine(string.Join(",",
                CsvCell(entry.Kind),
                CsvCell(entry.Reference),
                CsvCell(entry.Status),
                CsvCell(entry.Tone),
                CsvCell(entry.Severity ?? string.Empty),
                CsvCell(entry.CorrelationId),
                CsvCell(entry.DecisionId),
                CsvCell(entry.ExecutionAttemptId),
                CsvCell(entry.IncidentReference),
                CsvCell(entry.ApprovalReference),
                CsvCell(entry.UserId),
                CsvCell(entry.Symbol),
                CsvCell(entry.Title),
                CsvCell(entry.Summary),
                CsvCell(entry.Source),
                CsvCell(entry.CreatedAtUtc.ToString("O")),
                CsvCell(string.Join(" | ", entry.Tags))));
        }

        return builder.ToString();
    }

    private static string CsvCell(string? value)
    {
        var normalized = SensitivePayloadMasker.Mask(value, 2048) ?? string.Empty;

        if (normalized.Length > 0 &&
            ("=+-@".Contains(normalized[0]) || normalized[0] is '\t' or '\r' or '\n'))
        {
            normalized = $"'{normalized}";
        }

        normalized = normalized.Replace("\"", "\"\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

        return $"\"{normalized}\"";
    }

    private static string BuildFilterSummary(LogCenterQueryRequest request)
    {
        return string.Join("; ",
            BuildFilterSegment("Query", request.Query),
            BuildFilterSegment("CorrelationId", request.CorrelationId),
            BuildFilterSegment("DecisionId", request.DecisionId),
            BuildFilterSegment("ExecutionAttemptId", request.ExecutionAttemptId),
            BuildFilterSegment("UserId", request.UserId),
            BuildFilterSegment("Symbol", request.Symbol),
            BuildFilterSegment("Status", request.Status),
            BuildDateSegment("FromUtc", request.FromUtc),
            BuildDateSegment("ToUtc", request.ToUtc),
            $"Take={request.Take}");
    }

    private static string BuildKindSummary(IReadOnlyCollection<LogCenterEntrySnapshot> entries)
    {
        return string.Join("; ",
            entries
                .GroupBy(entry => entry.Kind)
                .Select(group => $"{group.Key}={group.Count()}"));
    }

    private static string BuildFilterSegment(string label, string? value)
    {
        var masked = SensitivePayloadMasker.Mask(value, 64);

        return string.IsNullOrWhiteSpace(masked)
            ? $"{label}=*"
            : $"{label}={Truncate(masked, 64)}";
    }

    private static string BuildDateSegment(string label, DateTime? value)
    {
        return value is null
            ? $"{label}=*"
            : $"{label}={value.Value.ToUniversalTime():O}";
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static int NormalizeTake(int take, int fallback)
    {
        return take is > 0 and <= 1000
            ? take
            : fallback;
    }

    private static string Truncate(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }
}
