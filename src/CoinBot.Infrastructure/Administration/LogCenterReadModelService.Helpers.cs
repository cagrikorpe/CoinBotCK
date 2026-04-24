using CoinBot.Application.Abstractions.Administration;

namespace CoinBot.Infrastructure.Administration;

public sealed partial class LogCenterReadModelService
{
    private static NormalizedLogCenterQuery Normalize(LogCenterQueryRequest request)
    {
        var query = NormalizeOptional(request.Query, 128);
        var correlationId = NormalizeOptional(request.CorrelationId, 128);
        var decisionId = NormalizeOptional(request.DecisionId, 64);
        var executionAttemptId = NormalizeOptional(request.ExecutionAttemptId, 64);
        var userId = NormalizeOptional(request.UserId, 450);
        var symbol = NormalizeOptional(request.Symbol, 32);
        var status = NormalizeOptional(request.Status, 64);
        var fromUtc = NormalizeDateStart(request.FromUtc);
        var toUtc = NormalizeDateEnd(request.ToUtc);
        var take = request.Take is > 0 and <= 1000 ? request.Take : 100;
        var page = request.Page >= 1 ? request.Page : 1;
        var pageSize = request.PageSize switch
        {
            <= 0 => 25,
            <= 10 => 10,
            <= 25 => 25,
            <= 50 => 50,
            _ => 100
        };
        var hasInvalidDateRange = fromUtc is not null && toUtc is not null && fromUtc > toUtc;

        return new NormalizedLogCenterQuery(
            query,
            correlationId,
            decisionId,
            executionAttemptId,
            userId,
            symbol,
            status,
            fromUtc,
            toUtc,
            take,
            page,
            pageSize,
            hasInvalidDateRange);
    }

    private static DateTime? NormalizeDateStart(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        return DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
    }

    private static DateTime? NormalizeDateEnd(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        return DateTime.SpecifyKind(value.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc);
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return null;
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }

    private static bool IsCritical(LogCenterEntrySnapshot entry)
    {
        return string.Equals(entry.Tone, "critical", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Severity, "Critical", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record NormalizedLogCenterQuery(
        string? Query,
        string? CorrelationId,
        string? DecisionId,
        string? ExecutionAttemptId,
        string? UserId,
        string? Symbol,
        string? Status,
        DateTime? FromUtc,
        DateTime? ToUtc,
        int Take,
        int Page,
        int PageSize,
        bool HasInvalidDateRange)
    {
        public string? QueryLower => Query?.ToLowerInvariant();
        public string? DecisionIdLower => DecisionId?.ToLowerInvariant();
        public string? ExecutionAttemptIdLower => ExecutionAttemptId?.ToLowerInvariant();
        public string? StatusLower => Status?.ToLowerInvariant();
        public string? SymbolLower => Symbol?.ToLowerInvariant();
        public bool HasSearchFilters =>
            !string.IsNullOrWhiteSpace(Query) ||
            !string.IsNullOrWhiteSpace(CorrelationId) ||
            !string.IsNullOrWhiteSpace(DecisionId) ||
            !string.IsNullOrWhiteSpace(ExecutionAttemptId) ||
            !string.IsNullOrWhiteSpace(UserId) ||
            !string.IsNullOrWhiteSpace(Symbol) ||
            !string.IsNullOrWhiteSpace(Status);
        public bool HasDateRange => FromUtc is not null || ToUtc is not null;

        public LogCenterQueryRequest ToRequest()
        {
            return new LogCenterQueryRequest(
                Query,
                CorrelationId,
                DecisionId,
                ExecutionAttemptId,
                UserId,
                Symbol,
                Status,
                FromUtc,
                ToUtc,
                Take,
                Page,
                PageSize);
        }

        public NormalizedLogCenterQuery WithDateRange(DateTime? fromUtc, DateTime? toUtc)
        {
            return this with
            {
                FromUtc = fromUtc,
                ToUtc = toUtc,
                HasInvalidDateRange = fromUtc is not null && toUtc is not null && fromUtc > toUtc
            };
        }
    }
}
