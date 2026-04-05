using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Infrastructure.Exchange;

public sealed class ExchangeAccountSyncStateService(ApplicationDbContext dbContext)
{
    internal async Task RecordConnectionStateAsync(
        ExchangeSyncAccountDescriptor account,
        ExchangePrivateStreamConnectionState connectionState,
        string? errorCode,
        int? consecutiveFailureCount = null,
        DateTime? listenKeyStartedAtUtc = null,
        DateTime? listenKeyRenewedAtUtc = null,
        DateTime? lastPrivateStreamEventAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        var state = await GetOrCreateAsync(account.ExchangeAccountId, account.OwnerUserId, account.Plane, cancellationToken);

        state.PrivateStreamConnectionState = connectionState;
        state.LastErrorCode = NormalizeOptional(errorCode);

        if (consecutiveFailureCount.HasValue)
        {
            state.ConsecutiveStreamFailureCount = consecutiveFailureCount.Value;
        }

        if (listenKeyStartedAtUtc.HasValue)
        {
            state.LastListenKeyStartedAtUtc = NormalizeTimestamp(listenKeyStartedAtUtc.Value);
        }

        if (listenKeyRenewedAtUtc.HasValue)
        {
            state.LastListenKeyRenewedAtUtc = NormalizeTimestamp(listenKeyRenewedAtUtc.Value);
        }

        if (lastPrivateStreamEventAtUtc.HasValue)
        {
            var normalizedLastPrivateStreamEventAtUtc = NormalizeTimestamp(lastPrivateStreamEventAtUtc.Value);
            state.LastPrivateStreamEventAtUtc = state.LastPrivateStreamEventAtUtc.HasValue &&
                                                state.LastPrivateStreamEventAtUtc.Value > normalizedLastPrivateStreamEventAtUtc
                ? state.LastPrivateStreamEventAtUtc.Value
                : normalizedLastPrivateStreamEventAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordBalanceSyncAsync(ExchangeAccountSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var state = await GetOrCreateAsync(snapshot.ExchangeAccountId, snapshot.OwnerUserId, snapshot.Plane, cancellationToken);
        state.LastBalanceSyncedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordPositionSyncAsync(ExchangeAccountSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var state = await GetOrCreateAsync(snapshot.ExchangeAccountId, snapshot.OwnerUserId, snapshot.Plane, cancellationToken);
        state.LastPositionSyncedAtUtc = NormalizeTimestamp(snapshot.ReceivedAtUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal async Task RecordReconciliationAsync(
        ExchangeSyncAccountDescriptor account,
        ExchangeStateDriftStatus driftStatus,
        string? driftSummary,
        DateTime reconciledAtUtc,
        DateTime? driftDetectedAtUtc,
        string? errorCode,
        CancellationToken cancellationToken = default)
    {
        var state = await GetOrCreateAsync(account.ExchangeAccountId, account.OwnerUserId, account.Plane, cancellationToken);
        state.LastStateReconciledAtUtc = NormalizeTimestamp(reconciledAtUtc);
        state.DriftStatus = driftStatus;
        state.DriftSummary = TrimToLength(driftSummary, 512);
        state.LastDriftDetectedAtUtc = driftDetectedAtUtc.HasValue
            ? NormalizeTimestamp(driftDetectedAtUtc.Value)
            : driftStatus == ExchangeStateDriftStatus.DriftDetected
                ? NormalizeTimestamp(reconciledAtUtc)
                : null;
        state.LastErrorCode = NormalizeOptional(errorCode);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<ExchangeAccountSyncState> GetOrCreateAsync(
        Guid exchangeAccountId,
        string ownerUserId,
        ExchangeDataPlane plane,
        CancellationToken cancellationToken)
    {
        var state = await dbContext.ExchangeAccountSyncStates
            .SingleOrDefaultAsync(
                entity => entity.ExchangeAccountId == exchangeAccountId &&
                          entity.Plane == plane &&
                          !entity.IsDeleted,
                cancellationToken);

        if (state is not null)
        {
            return state;
        }

        state = new ExchangeAccountSyncState
        {
            OwnerUserId = ownerUserId.Trim(),
            ExchangeAccountId = exchangeAccountId,
            Plane = plane
        };

        dbContext.ExchangeAccountSyncStates.Add(state);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return state;
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(state).State = EntityState.Detached;

            return await dbContext.ExchangeAccountSyncStates
                .SingleAsync(
                    entity => entity.ExchangeAccountId == exchangeAccountId &&
                              entity.Plane == plane &&
                              !entity.IsDeleted,
                    cancellationToken);
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string? TrimToLength(string? value, int maxLength)
    {
        var normalizedValue = NormalizeOptional(value);

        if (normalizedValue is null)
        {
            return null;
        }

        return normalizedValue.Length <= maxLength
            ? normalizedValue
            : normalizedValue[..maxLength];
    }

    private static DateTime NormalizeTimestamp(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}

