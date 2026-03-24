using System.Diagnostics;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Administration;

public sealed class TraceService(
    ApplicationDbContext dbContext,
    ICorrelationContextAccessor correlationContextAccessor,
    TimeProvider timeProvider) : ITraceService
{
    public async Task<DecisionTraceSnapshot> WriteDecisionTraceAsync(
        DecisionTraceWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var entity = new DecisionTrace
        {
            StrategySignalId = request.StrategySignalId,
            CorrelationId = NormalizeRequired(ResolveCorrelationId(request.CorrelationId), 128, nameof(request.CorrelationId)),
            DecisionId = NormalizeRequired(ResolveDecisionId(request.DecisionId), 64, nameof(request.DecisionId)),
            UserId = NormalizeRequired(request.UserId, 450, nameof(request.UserId)),
            Symbol = NormalizeRequired(request.Symbol, 32, nameof(request.Symbol)),
            Timeframe = NormalizeRequired(request.Timeframe, 16, nameof(request.Timeframe)),
            StrategyVersion = NormalizeRequired(request.StrategyVersion, 128, nameof(request.StrategyVersion)),
            SignalType = NormalizeRequired(request.SignalType, 32, nameof(request.SignalType)),
            RiskScore = request.RiskScore,
            DecisionOutcome = NormalizeRequired(request.DecisionOutcome, 64, nameof(request.DecisionOutcome)),
            VetoReasonCode = NormalizeOptional(request.VetoReasonCode, 64),
            LatencyMs = Math.Max(0, request.LatencyMs),
            SnapshotJson = NormalizeRequired(
                SensitivePayloadMasker.Mask(request.SnapshotJson, 8192),
                8192,
                nameof(request.SnapshotJson)),
            CreatedAtUtc = request.CreatedAtUtc?.ToUniversalTime() ?? timeProvider.GetUtcNow().UtcDateTime
        };

        dbContext.DecisionTraces.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Map(entity);
    }

    public async Task<ExecutionTraceSnapshot> WriteExecutionTraceAsync(
        ExecutionTraceWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var entity = new ExecutionTrace
        {
            ExecutionOrderId = request.ExecutionOrderId,
            CorrelationId = NormalizeRequired(ResolveCorrelationId(request.CorrelationId), 128, nameof(request.CorrelationId)),
            ExecutionAttemptId = NormalizeRequired(ResolveExecutionAttemptId(request.ExecutionAttemptId), 64, nameof(request.ExecutionAttemptId)),
            CommandId = NormalizeRequired(request.CommandId, 128, nameof(request.CommandId)),
            UserId = NormalizeRequired(request.UserId, 450, nameof(request.UserId)),
            Provider = NormalizeRequired(request.Provider, 64, nameof(request.Provider)),
            Endpoint = NormalizeRequired(
                SensitivePayloadMasker.Mask(request.Endpoint, 256),
                256,
                nameof(request.Endpoint)),
            RequestMasked = NormalizeOptional(SensitivePayloadMasker.Mask(request.RequestMasked, 4096), 4096),
            ResponseMasked = NormalizeOptional(SensitivePayloadMasker.Mask(request.ResponseMasked, 4096), 4096),
            HttpStatusCode = request.HttpStatusCode,
            ExchangeCode = NormalizeOptional(request.ExchangeCode, 64),
            LatencyMs = request.LatencyMs.HasValue
                ? Math.Max(0, request.LatencyMs.Value)
                : null,
            CreatedAtUtc = request.CreatedAtUtc?.ToUniversalTime() ?? timeProvider.GetUtcNow().UtcDateTime
        };

        dbContext.ExecutionTraces.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Map(entity);
    }

    public async Task<DecisionTraceSnapshot?> GetDecisionTraceByStrategySignalIdAsync(
        Guid strategySignalId,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.DecisionTraces
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(item => !item.IsDeleted && item.StrategySignalId == strategySignalId)
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return entity is null
            ? null
            : Map(entity);
    }

    public async Task<IReadOnlyCollection<AdminTraceListItem>> SearchAsync(
        AdminTraceSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedTake = request.Take is > 0 and <= 200
            ? request.Take
            : 50;
        var normalizedQuery = NormalizeOptional(request.Query, 128);
        var normalizedCorrelationId = NormalizeOptional(request.CorrelationId, 128);
        var normalizedDecisionId = NormalizeOptional(request.DecisionId, 64);
        var normalizedExecutionAttemptId = NormalizeOptional(request.ExecutionAttemptId, 64);
        var normalizedUserId = NormalizeOptional(request.UserId, 450);

        var decisionQuery = dbContext.DecisionTraces
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted);
        var executionQuery = dbContext.ExecutionTraces
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted);

        if (!string.IsNullOrWhiteSpace(normalizedCorrelationId))
        {
            decisionQuery = decisionQuery.Where(entity => entity.CorrelationId == normalizedCorrelationId);
            executionQuery = executionQuery.Where(entity => entity.CorrelationId == normalizedCorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(normalizedDecisionId))
        {
            decisionQuery = decisionQuery.Where(entity => entity.DecisionId == normalizedDecisionId);
        }

        if (!string.IsNullOrWhiteSpace(normalizedExecutionAttemptId))
        {
            executionQuery = executionQuery.Where(entity => entity.ExecutionAttemptId == normalizedExecutionAttemptId);
        }

        if (!string.IsNullOrWhiteSpace(normalizedUserId))
        {
            decisionQuery = decisionQuery.Where(entity => entity.UserId == normalizedUserId);
            executionQuery = executionQuery.Where(entity => entity.UserId == normalizedUserId);
        }

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            decisionQuery = decisionQuery.Where(entity =>
                entity.CorrelationId == normalizedQuery ||
                entity.DecisionId == normalizedQuery ||
                entity.UserId == normalizedQuery);
            executionQuery = executionQuery.Where(entity =>
                entity.CorrelationId == normalizedQuery ||
                entity.ExecutionAttemptId == normalizedQuery ||
                entity.CommandId == normalizedQuery ||
                entity.UserId == normalizedQuery);
        }

        var decisionTraces = await decisionQuery
            .OrderByDescending(entity => entity.CreatedAtUtc)
            .Take(normalizedTake * 4)
            .ToListAsync(cancellationToken);
        var executionTraces = await executionQuery
            .OrderByDescending(entity => entity.CreatedAtUtc)
            .Take(normalizedTake * 4)
            .ToListAsync(cancellationToken);
        var rows = new Dictionary<string, TraceListItemBuilder>(StringComparer.Ordinal);

        foreach (var trace in decisionTraces)
        {
            if (!rows.TryGetValue(trace.CorrelationId, out var builder))
            {
                builder = new TraceListItemBuilder(trace.CorrelationId);
                rows.Add(trace.CorrelationId, builder);
            }

            builder.DecisionCount++;
            builder.UserId ??= trace.UserId;
            builder.Symbol ??= trace.Symbol;
            builder.Timeframe ??= trace.Timeframe;
            builder.StrategyVersion ??= trace.StrategyVersion;
            builder.DecisionOutcome = trace.DecisionOutcome;
            builder.VetoReasonCode ??= trace.VetoReasonCode;
            builder.LastUpdatedAtUtc = builder.LastUpdatedAtUtc < trace.CreatedAtUtc
                ? trace.CreatedAtUtc
                : builder.LastUpdatedAtUtc;
        }

        foreach (var trace in executionTraces)
        {
            if (!rows.TryGetValue(trace.CorrelationId, out var builder))
            {
                builder = new TraceListItemBuilder(trace.CorrelationId);
                rows.Add(trace.CorrelationId, builder);
            }

            builder.ExecutionCount++;
            builder.UserId ??= trace.UserId;
            builder.LatestExecutionProvider = trace.Provider;
            builder.LastUpdatedAtUtc = builder.LastUpdatedAtUtc < trace.CreatedAtUtc
                ? trace.CreatedAtUtc
                : builder.LastUpdatedAtUtc;
        }

        return rows.Values
            .OrderByDescending(item => item.LastUpdatedAtUtc)
            .Take(normalizedTake)
            .Select(item => item.Build())
            .ToArray();
    }

    public async Task<AdminTraceDetailSnapshot?> GetDetailAsync(
        string correlationId,
        string? decisionId = null,
        string? executionAttemptId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCorrelationId = NormalizeRequired(correlationId, 128, nameof(correlationId));
        var normalizedDecisionId = NormalizeOptional(decisionId, 64);
        var normalizedExecutionAttemptId = NormalizeOptional(executionAttemptId, 64);

        var decisionQuery = dbContext.DecisionTraces
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted && entity.CorrelationId == normalizedCorrelationId);
        var executionQuery = dbContext.ExecutionTraces
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => !entity.IsDeleted && entity.CorrelationId == normalizedCorrelationId);

        if (!string.IsNullOrWhiteSpace(normalizedDecisionId))
        {
            decisionQuery = decisionQuery.Where(entity => entity.DecisionId == normalizedDecisionId);
        }

        if (!string.IsNullOrWhiteSpace(normalizedExecutionAttemptId))
        {
            executionQuery = executionQuery.Where(entity => entity.ExecutionAttemptId == normalizedExecutionAttemptId);
        }

        var decisionTraces = await decisionQuery
            .OrderByDescending(entity => entity.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var executionTraces = await executionQuery
            .OrderByDescending(entity => entity.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        if (decisionTraces.Count == 0 && executionTraces.Count == 0)
        {
            return null;
        }

        return new AdminTraceDetailSnapshot(
            normalizedCorrelationId,
            decisionTraces.Select(Map).ToArray(),
            executionTraces.Select(Map).ToArray());
    }

    private string ResolveCorrelationId(string? correlationId)
    {
        var normalizedCorrelationId = NormalizeOptional(correlationId, 128);

        if (!string.IsNullOrWhiteSpace(normalizedCorrelationId))
        {
            return normalizedCorrelationId;
        }

        var scopedCorrelationId = correlationContextAccessor.Current?.CorrelationId;

        if (!string.IsNullOrWhiteSpace(scopedCorrelationId))
        {
            return scopedCorrelationId.Trim();
        }

        var activityTraceId = Activity.Current?.TraceId.ToString();

        return string.IsNullOrWhiteSpace(activityTraceId)
            ? Guid.NewGuid().ToString("N")
            : activityTraceId;
    }

    private static string ResolveDecisionId(string? decisionId)
    {
        var normalizedDecisionId = NormalizeOptional(decisionId, 64);
        return string.IsNullOrWhiteSpace(normalizedDecisionId)
            ? $"dec_{Guid.NewGuid():N}"[..32]
            : normalizedDecisionId;
    }

    private static string ResolveExecutionAttemptId(string? executionAttemptId)
    {
        var normalizedExecutionAttemptId = NormalizeOptional(executionAttemptId, 64);
        return string.IsNullOrWhiteSpace(normalizedExecutionAttemptId)
            ? $"xea_{Guid.NewGuid():N}"[..32]
            : normalizedExecutionAttemptId;
    }

    private static DecisionTraceSnapshot Map(DecisionTrace entity)
    {
        return new DecisionTraceSnapshot(
            entity.Id,
            entity.StrategySignalId,
            entity.CorrelationId,
            entity.DecisionId,
            entity.UserId,
            entity.Symbol,
            entity.Timeframe,
            entity.StrategyVersion,
            entity.SignalType,
            entity.RiskScore,
            entity.DecisionOutcome,
            entity.VetoReasonCode,
            entity.LatencyMs,
            entity.SnapshotJson,
            entity.CreatedAtUtc);
    }

    private static ExecutionTraceSnapshot Map(ExecutionTrace entity)
    {
        return new ExecutionTraceSnapshot(
            entity.Id,
            entity.ExecutionOrderId,
            entity.CorrelationId,
            entity.ExecutionAttemptId,
            entity.CommandId,
            entity.UserId,
            entity.Provider,
            entity.Endpoint,
            entity.RequestMasked,
            entity.ResponseMasked,
            entity.HttpStatusCode,
            entity.ExchangeCode,
            entity.LatencyMs,
            entity.CreatedAtUtc);
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
    {
        var normalizedValue = NormalizeOptional(value, maxLength);

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
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

    private sealed class TraceListItemBuilder(string correlationId)
    {
        public string CorrelationId { get; } = correlationId;

        public string? UserId { get; set; }

        public string? Symbol { get; set; }

        public string? Timeframe { get; set; }

        public string? StrategyVersion { get; set; }

        public string? DecisionOutcome { get; set; }

        public string? VetoReasonCode { get; set; }

        public string? LatestExecutionProvider { get; set; }

        public int DecisionCount { get; set; }

        public int ExecutionCount { get; set; }

        public DateTime LastUpdatedAtUtc { get; set; }

        public AdminTraceListItem Build()
        {
            return new AdminTraceListItem(
                CorrelationId,
                UserId,
                Symbol,
                Timeframe,
                StrategyVersion,
                DecisionOutcome,
                VetoReasonCode,
                LatestExecutionProvider,
                DecisionCount,
                ExecutionCount,
                LastUpdatedAtUtc);
        }
    }
}
