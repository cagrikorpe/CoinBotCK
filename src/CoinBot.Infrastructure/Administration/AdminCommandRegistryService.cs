using CoinBot.Application.Abstractions.Administration;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Administration;

public sealed class AdminCommandRegistryService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IAdminCommandRegistry
{
    public async Task<AdminCommandStartResult> TryStartAsync(
        AdminCommandStartRequest request,
        CancellationToken cancellationToken = default)
    {
        var commandId = NormalizeRequired(request.CommandId, 128, nameof(request.CommandId));
        var commandType = NormalizeRequired(request.CommandType, 128, nameof(request.CommandType));
        var actorUserId = NormalizeRequired(request.ActorUserId, 450, nameof(request.ActorUserId));
        var scopeKey = NormalizeRequired(request.ScopeKey, 256, nameof(request.ScopeKey));
        var payloadHash = NormalizeRequired(request.PayloadHash, 128, nameof(request.PayloadHash));
        var correlationId = NormalizeOptional(request.CorrelationId, 128);

        var existingEntry = await dbContext.AdminCommandRegistryEntries
            .SingleOrDefaultAsync(entity => entity.CommandId == commandId, cancellationToken);

        if (existingEntry is not null)
        {
            if (!string.Equals(existingEntry.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return new AdminCommandStartResult(
                    AdminCommandStartDisposition.PayloadConflict,
                    existingEntry.Status,
                    existingEntry.ResultSummary);
            }

            return existingEntry.Status == AdminCommandStatus.Running
                ? new AdminCommandStartResult(
                    AdminCommandStartDisposition.AlreadyRunning,
                    existingEntry.Status,
                    existingEntry.ResultSummary)
                : new AdminCommandStartResult(
                    AdminCommandStartDisposition.AlreadyCompleted,
                    existingEntry.Status,
                    existingEntry.ResultSummary);
        }

        var runningScopeEntry = await dbContext.AdminCommandRegistryEntries
            .AsNoTracking()
            .Where(entity =>
                entity.CommandType == commandType &&
                entity.ScopeKey == scopeKey &&
                entity.Status == AdminCommandStatus.Running)
            .OrderByDescending(entity => entity.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (runningScopeEntry is not null)
        {
            return new AdminCommandStartResult(
                AdminCommandStartDisposition.AlreadyRunning,
                runningScopeEntry.Status,
                runningScopeEntry.ResultSummary);
        }

        var completedScopeEntry = await dbContext.AdminCommandRegistryEntries
            .AsNoTracking()
            .Where(entity =>
                entity.CommandType == commandType &&
                entity.ScopeKey == scopeKey &&
                entity.PayloadHash == payloadHash &&
                entity.Status == AdminCommandStatus.Completed)
            .OrderByDescending(entity => entity.CompletedAtUtc ?? entity.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (completedScopeEntry is not null)
        {
            return new AdminCommandStartResult(
                AdminCommandStartDisposition.AlreadyCompleted,
                completedScopeEntry.Status,
                completedScopeEntry.ResultSummary);
        }

        dbContext.AdminCommandRegistryEntries.Add(new AdminCommandRegistryEntry
        {
            Id = Guid.NewGuid(),
            CommandId = commandId,
            CommandType = commandType,
            ActorUserId = actorUserId,
            ScopeKey = scopeKey,
            PayloadHash = payloadHash,
            Status = AdminCommandStatus.Running,
            StartedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            CorrelationId = correlationId
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return new AdminCommandStartResult(
            AdminCommandStartDisposition.Started,
            AdminCommandStatus.Running,
            ResultSummary: null);
    }

    public async Task CompleteAsync(
        AdminCommandCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var commandId = NormalizeRequired(request.CommandId, 128, nameof(request.CommandId));
        var payloadHash = NormalizeRequired(request.PayloadHash, 128, nameof(request.PayloadHash));
        var correlationId = NormalizeOptional(request.CorrelationId, 128);
        var resultSummary = NormalizeOptional(request.ResultSummary, 512);

        if (request.Status == AdminCommandStatus.Running)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Status), "Completion status cannot remain Running.");
        }

        var existingEntry = await dbContext.AdminCommandRegistryEntries
            .SingleOrDefaultAsync(entity => entity.CommandId == commandId, cancellationToken)
            ?? throw new InvalidOperationException($"Admin command '{commandId}' was not found.");

        if (!string.Equals(existingEntry.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Admin command payload hash mismatch detected.");
        }

        existingEntry.Status = request.Status;
        existingEntry.ResultSummary = resultSummary;
        existingEntry.CompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            existingEntry.CorrelationId = correlationId;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeRequired(string? value, int maxLength, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : throw new ArgumentOutOfRangeException(parameterName, $"The value cannot exceed {maxLength} characters.");
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
            : throw new ArgumentOutOfRangeException(nameof(value), $"The value cannot exceed {maxLength} characters.");
    }
}
