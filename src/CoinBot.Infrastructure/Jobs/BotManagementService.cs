using System.Globalization;
using CoinBot.Application.Abstractions.Bots;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Infrastructure.Strategies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BotManagementService(
    ApplicationDbContext dbContext,
    IBotPilotControlService botPilotControlService,
    ICriticalUserOperationAuthorizer criticalUserOperationAuthorizer,
    TimeProvider timeProvider,
    IOptions<BotExecutionPilotOptions> options,
    IOptions<DataLatencyGuardOptions> dataLatencyGuardOptions,
    UserOperationsStreamHub? userOperationsStreamHub = null) : IBotManagementService
{
    private const string FrozenPilotMarginType = "ISOLATED";
    private const int LastExecutionBlockDetailMaxLength = 240;
    private readonly BotExecutionPilotOptions optionsValue = options.Value;
    private readonly int staleThresholdMilliseconds = checked(dataLatencyGuardOptions.Value.StaleDataThresholdSeconds * 1000);

    public async Task<BotManagementPageSnapshot> GetPageAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        ownerUserId = dbContext.EnsureCurrentUserScope(ownerUserId);

        var bots = await dbContext.TradingBots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == ownerUserId && !entity.IsDeleted)
            .OrderBy(entity => entity.Name)
            .ThenBy(entity => entity.Id)
            .ToListAsync(cancellationToken);

        var botIds = bots.Select(entity => entity.Id).ToArray();
        var strategyKeys = bots
            .Select(entity => entity.StrategyKey)
            .Where(entity => !string.IsNullOrWhiteSpace(entity))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var exchangeAccountIds = bots
            .Where(entity => entity.ExchangeAccountId.HasValue)
            .Select(entity => entity.ExchangeAccountId!.Value)
            .Distinct()
            .ToArray();

        var strategies = await dbContext.TradingStrategies
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted &&
                strategyKeys.Contains(entity.StrategyKey))
            .ToListAsync(cancellationToken);

        var accounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted &&
                exchangeAccountIds.Contains(entity.Id))
            .ToListAsync(cancellationToken);

        var states = await dbContext.BackgroundJobStates
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.JobType == BackgroundJobTypes.BotExecution &&
                entity.BotId.HasValue &&
                botIds.Contains(entity.BotId.Value))
            .ToListAsync(cancellationToken);

        var latestOrders = await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.BotId.HasValue &&
                botIds.Contains(entity.BotId.Value))
            .OrderByDescending(entity => entity.CreatedDate)
            .ToListAsync(cancellationToken);
        List<AiShadowDecision> latestShadowDecisions = botIds.Length == 0
            ? []
            : await dbContext.AiShadowDecisions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    botIds.Contains(entity.BotId) &&
                    !entity.IsDeleted)
                .OrderByDescending(entity => entity.EvaluatedAtUtc)
                .ThenByDescending(entity => entity.CreatedDate)
                .ToListAsync(cancellationToken);

        var strategiesByKey = strategies.ToDictionary(entity => entity.StrategyKey, StringComparer.Ordinal);
        var publishedStrategyIdSet = await LoadRuntimeReadyStrategyIdSetAsync(strategies, cancellationToken);
        var accountsById = accounts.ToDictionary(entity => entity.Id);
        var statesByBotId = states
            .Where(entity => entity.BotId.HasValue)
            .GroupBy(entity => entity.BotId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entity => entity.UpdatedDate).First());
        var ordersByBotId = latestOrders
            .Where(entity => entity.BotId.HasValue)
            .GroupBy(entity => entity.BotId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.First());
        var shadowDecisionsByBotId = latestShadowDecisions
            .GroupBy(entity => entity.BotId)
            .ToDictionary(
                group => group.Key,
                group => group.First());

        var latestOrderIds = ordersByBotId.Values
            .Select(entity => entity.Id)
            .ToArray();
        var latestTransitionSnapshotsByOrderId = latestOrderIds.Length == 0
            ? new Dictionary<Guid, LastExecutionTransitionSnapshot>()
            : (await dbContext.ExecutionOrderTransitions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    !entity.IsDeleted &&
                    latestOrderIds.Contains(entity.ExecutionOrderId))
                .OrderByDescending(entity => entity.SequenceNumber)
                .ToListAsync(cancellationToken))
            .GroupBy(entity => entity.ExecutionOrderId)
            .ToDictionary(
                group => group.Key,
                group => CreateLastExecutionTransitionSnapshot(group));
        var degradedModeStateIds = ordersByBotId
            .Select(pair =>
            {
                latestTransitionSnapshotsByOrderId.TryGetValue(pair.Value.Id, out var transitionSnapshot);
                return ResolveDegradedModeStateId(pair.Value, transitionSnapshot);
            })
            .Where(stateId => stateId.HasValue)
            .Select(stateId => stateId!.Value)
            .Distinct()
            .ToArray();
        var degradedModeStatesById = degradedModeStateIds.Length == 0
            ? new Dictionary<Guid, DegradedModeState>()
            : await dbContext.DegradedModeStates
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity => degradedModeStateIds.Contains(entity.Id))
                .ToDictionaryAsync(entity => entity.Id, cancellationToken);
        var utcNow = timeProvider.GetUtcNow().UtcDateTime;

        var snapshots = bots
            .Select(bot =>
            {
                strategiesByKey.TryGetValue(bot.StrategyKey, out var strategy);
                ExchangeAccount? account = null;

                if (bot.ExchangeAccountId.HasValue)
                {
                    accountsById.TryGetValue(bot.ExchangeAccountId.Value, out account);
                }

                statesByBotId.TryGetValue(bot.Id, out var state);
                ordersByBotId.TryGetValue(bot.Id, out var order);
                shadowDecisionsByBotId.TryGetValue(bot.Id, out var shadowDecision);
                var cooldownBlockedUntilUtc = ResolveCooldownBlockedUntilUtc(bot, order, utcNow);
                var cooldownRemainingSeconds = ResolveCooldownRemainingSeconds(cooldownBlockedUntilUtc, utcNow);
                LastExecutionTransitionSnapshot? lastExecutionTransition = null;

                if (order?.State is ExecutionOrderState.Rejected or ExecutionOrderState.Failed)
                {
                    latestTransitionSnapshotsByOrderId.TryGetValue(order.Id, out lastExecutionTransition);
                }
                else if (order is not null)
                {
                    latestTransitionSnapshotsByOrderId.TryGetValue(order.Id, out lastExecutionTransition);
                }

                var degradedModeStateId = ResolveDegradedModeStateId(order, lastExecutionTransition);
                var degradedModeState = degradedModeStateId.HasValue &&
                    degradedModeStatesById.TryGetValue(degradedModeStateId.Value, out var resolvedDegradedModeState)
                    ? resolvedDegradedModeState
                    : null;
                var latencyReasonCode = lastExecutionTransition?.Diagnostics.LatencyReasonCode ??
                    degradedModeState?.ReasonCode.ToString();
                var decisionAtUtc = lastExecutionTransition?.OccurredAtUtc ?? order?.UpdatedDate;
                var isBlockedDecision = order?.State is ExecutionOrderState.Rejected or ExecutionOrderState.Failed;
                var decisionReasonCode = order is null
                    ? null
                    : ExecutionDecisionDiagnostics.ResolveDecisionReasonCode(
                        isBlockedDecision,
                        order.FailureCode ?? lastExecutionTransition?.EventCode,
                        latencyReasonCode);
                var decisionReasonType = decisionReasonCode is null
                    ? null
                    : ExecutionDecisionDiagnostics.ResolveDecisionReasonType(decisionReasonCode, latencyReasonCode);
                var decisionSummary = decisionReasonCode is null
                    ? null
                    : ExecutionDecisionDiagnostics.ResolveDecisionSummary(
                        isBlockedDecision,
                        decisionReasonType ?? "Other",
                        decisionReasonCode,
                        lastExecutionTransition?.Diagnostics.BlockDetail ?? order?.FailureDetail);
                var lastExecutionLastCandleAtUtc = lastExecutionTransition?.Diagnostics.LastCandleAtUtc ??
                    NormalizeUtcNullable(degradedModeState?.LatestDataTimestampAtUtc);
                var lastExecutionDataAgeMilliseconds = lastExecutionTransition?.Diagnostics.DataAgeMilliseconds ??
                    ExecutionDecisionDiagnostics.ResolveDecisionDataAgeMilliseconds(
                        degradedModeState,
                        decisionAtUtc,
                        order?.FailureDetail);
                var lastExecutionContinuityGapCount = lastExecutionTransition?.Diagnostics.ContinuityGapCount ??
                    degradedModeState?.LatestContinuityGapCount;
                var lastExecutionContinuityRecoveredAtUtc = NormalizeUtcNullable(degradedModeState?.LatestContinuityRecoveredAtUtc);
                var lastExecutionContinuityState = lastExecutionTransition?.Diagnostics.ContinuityState ??
                    ExecutionDecisionDiagnostics.ResolveContinuityState(
                        latencyReasonCode,
                        lastExecutionContinuityGapCount,
                        lastExecutionContinuityRecoveredAtUtc);
                var lastExecutionStaleReason = lastExecutionTransition?.Diagnostics.StaleReason ??
                    ExecutionDecisionDiagnostics.ResolveStaleReason(latencyReasonCode);

                return new BotManagementBotSnapshot(
                    bot.Id,
                    bot.Name,
                    bot.StrategyKey,
                    strategy?.DisplayName,
                    strategy is not null && publishedStrategyIdSet.Contains(strategy.Id),
                    NormalizeSymbol(bot.Symbol),
                    bot.Quantity,
                    bot.ExchangeAccountId,
                    account?.DisplayName,
                    account?.CredentialStatus == ExchangeCredentialStatus.Active,
                    account is not null && !account.IsReadOnly,
                    bot.Leverage,
                    NormalizeMarginType(bot.MarginType),
                    bot.IsEnabled,
                    bot.OpenOrderCount,
                    bot.OpenPositionCount,
                    state?.Status.ToString(),
                    state?.LastErrorCode,
                    order?.State.ToString(),
                    order?.FailureCode,
                    order?.State is ExecutionOrderState.Rejected or ExecutionOrderState.Failed
                        ? lastExecutionTransition?.Diagnostics.BlockDetail
                        : null,
                    order?.RejectionStage.ToString(),
                    order?.SubmittedToBroker ?? false,
                    order?.RetryEligible ?? false,
                    order?.CooldownApplied ?? false,
                    order?.ReduceOnly ?? false,
                    order?.StopLossPrice.HasValue ?? false,
                    order?.TakeProfitPrice.HasValue ?? false,
                    order?.DuplicateSuppressed ?? false,
                    lastExecutionTransition?.EventCode,
                    NormalizeOptionalExecutionDiagnosticValue(
                        lastExecutionTransition?.CorrelationId,
                        toUpperInvariant: false),
                    lastExecutionTransition?.ClientOrderId,
                    cooldownBlockedUntilUtc,
                    cooldownRemainingSeconds,
                    order?.UpdatedDate,
                    bot.UpdatedDate,
                    lastExecutionLastCandleAtUtc,
                    lastExecutionDataAgeMilliseconds,
                    lastExecutionContinuityState,
                    lastExecutionContinuityGapCount,
                    lastExecutionStaleReason,
                    lastExecutionTransition?.Diagnostics.AffectedSymbol,
                    lastExecutionTransition?.Diagnostics.AffectedTimeframe,
                    order is null ? null : ExecutionDecisionDiagnostics.ResolveDecisionOutcome(isBlockedDecision),
                    decisionAtUtc,
                    decisionReasonType,
                    decisionReasonCode,
                    decisionSummary,
                    staleThresholdMilliseconds,
                    NormalizeUtcNullable(degradedModeState?.LatestContinuityGapStartedAtUtc),
                    NormalizeUtcNullable(degradedModeState?.LatestContinuityGapLastSeenAtUtc),
                    lastExecutionContinuityRecoveredAtUtc,
                    shadowDecision?.FinalAction,
                    shadowDecision?.NoSubmitReason,
                    NormalizeUtcNullable(shadowDecision?.EvaluatedAtUtc));
            })
            .ToArray();

        return new BotManagementPageSnapshot(snapshots);
    }

    public async Task<BotManagementEditorSnapshot> GetCreateEditorAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        ownerUserId = dbContext.EnsureCurrentUserScope(ownerUserId);

        var draft = new BotManagementDraftSnapshot(
            Name: string.Empty,
            StrategyKey: string.Empty,
            Symbol: NormalizeSymbol(optionsValue.DefaultSymbol),
            Quantity: null,
            ExchangeAccountId: await ResolveSingleActiveExchangeAccountIdAsync(ownerUserId, cancellationToken),
            Leverage: 1m,
            MarginType: FrozenPilotMarginType,
            IsEnabled: false);

        return await CreateEditorSnapshotAsync(ownerUserId, null, draft, cancellationToken);
    }

    public async Task<BotManagementEditorSnapshot?> GetEditEditorAsync(string ownerUserId, Guid botId, CancellationToken cancellationToken = default)
    {
        ownerUserId = dbContext.EnsureCurrentUserScope(ownerUserId);

        var bot = await dbContext.TradingBots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.Id == botId &&
                          entity.OwnerUserId == ownerUserId &&
                          !entity.IsDeleted,
                cancellationToken);

        if (bot is null)
        {
            return null;
        }

        var draft = new BotManagementDraftSnapshot(
            bot.Name,
            bot.StrategyKey,
            NormalizeSymbol(bot.Symbol),
            bot.Quantity,
            bot.ExchangeAccountId,
            bot.Leverage ?? 1m,
            NormalizeMarginType(bot.MarginType),
            bot.IsEnabled);

        return await CreateEditorSnapshotAsync(ownerUserId, bot.Id, draft, cancellationToken);
    }

    public async Task<BotManagementSaveResult> CreateAsync(
        string ownerUserId,
        BotManagementSaveCommand command,
        string actor,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ownerUserId = dbContext.EnsureCurrentUserScope(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var validationError = await ValidateCommandAsync(ownerUserId, null, command, cancellationToken);

        if (validationError is not null)
        {
            return validationError;
        }

        var authorization = await criticalUserOperationAuthorizer.AuthorizeAsync(
            new CriticalUserOperationAuthorizationRequest(
                ownerUserId,
                actor,
                "Bots.Create",
                $"User/{ownerUserId}/Bots",
                correlationId),
            cancellationToken);

        if (!authorization.IsAuthorized)
        {
            return new BotManagementSaveResult(
                null,
                false,
                false,
                false,
                authorization.FailureCode,
                authorization.FailureReason);
        }

        var bot = new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = NormalizeRequired(command.Name, nameof(command.Name)),
            StrategyKey = NormalizeRequired(command.StrategyKey, nameof(command.StrategyKey)),
            Symbol = NormalizeSymbol(command.Symbol),
            Quantity = NormalizeQuantity(command.Quantity),
            ExchangeAccountId = command.ExchangeAccountId,
            Leverage = NormalizeLeverage(command.Leverage),
            MarginType = NormalizeMarginType(command.MarginType),
            IsEnabled = false
        };

        dbContext.TradingBots.Add(bot);
        await dbContext.SaveChangesAsync(cancellationToken);
        userOperationsStreamHub?.Publish(
            new UserOperationsUpdate(
                ownerUserId,
                "BotChanged",
                bot.Id,
                null,
                "Created",
                null,
                timeProvider.GetUtcNow().UtcDateTime));

        return await ApplyEnabledStateAsync(ownerUserId, bot, command.IsEnabled, actor, correlationId, cancellationToken);
    }

    public async Task<BotManagementSaveResult> UpdateAsync(
        string ownerUserId,
        Guid botId,
        BotManagementSaveCommand command,
        string actor,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ownerUserId = dbContext.EnsureCurrentUserScope(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var bot = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.Id == botId &&
                          entity.OwnerUserId == ownerUserId &&
                          !entity.IsDeleted,
                cancellationToken);

        if (bot is null)
        {
            return new BotManagementSaveResult(null, false, false, false, "BotNotFound", "Bot bulunamadı.");
        }

        var authorization = await criticalUserOperationAuthorizer.AuthorizeAsync(
            new CriticalUserOperationAuthorizationRequest(
                ownerUserId,
                actor,
                "Bots.Update",
                $"TradingBot/{bot.Id:D}",
                correlationId),
            cancellationToken);

        if (!authorization.IsAuthorized)
        {
            return new BotManagementSaveResult(
                bot.Id,
                false,
                false,
                bot.IsEnabled,
                authorization.FailureCode,
                authorization.FailureReason);
        }

        var validationError = await ValidateCommandAsync(ownerUserId, botId, command, cancellationToken);

        if (validationError is not null)
        {
            return validationError with { BotId = botId };
        }

        var wasEnabled = bot.IsEnabled;
        bot.Name = NormalizeRequired(command.Name, nameof(command.Name));
        bot.StrategyKey = NormalizeRequired(command.StrategyKey, nameof(command.StrategyKey));
        bot.Symbol = NormalizeSymbol(command.Symbol);
        bot.Quantity = NormalizeQuantity(command.Quantity);
        bot.ExchangeAccountId = command.ExchangeAccountId;
        bot.Leverage = NormalizeLeverage(command.Leverage);
        bot.MarginType = NormalizeMarginType(command.MarginType);

        if (command.IsEnabled)
        {
            bot.IsEnabled = false;
            await dbContext.SaveChangesAsync(cancellationToken);
            return await ApplyEnabledStateAsync(ownerUserId, bot, true, actor, correlationId, cancellationToken);
        }

        if (wasEnabled)
        {
            var toggleResult = await botPilotControlService.SetEnabledAsync(
                ownerUserId,
                bot.Id,
                false,
                actor,
                correlationId,
                cancellationToken);

            return new BotManagementSaveResult(
                bot.Id,
                toggleResult.IsSuccessful,
                true,
                false,
                toggleResult.FailureCode,
                toggleResult.FailureReason);
        }

        ResetSchedulerState(bot.Id, isEnabled: false);
        await dbContext.SaveChangesAsync(cancellationToken);
        userOperationsStreamHub?.Publish(
            new UserOperationsUpdate(
                ownerUserId,
                "BotChanged",
                bot.Id,
                null,
                "Updated",
                null,
                timeProvider.GetUtcNow().UtcDateTime));

        return new BotManagementSaveResult(bot.Id, true, true, false, null, null);
    }

    private async Task<BotManagementSaveResult> ApplyEnabledStateAsync(
        string ownerUserId,
        TradingBot bot,
        bool shouldEnable,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (!shouldEnable)
        {
            ResetSchedulerState(bot.Id, isEnabled: false);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new BotManagementSaveResult(bot.Id, true, true, false, null, null);
        }

        var toggleResult = await botPilotControlService.SetEnabledAsync(
            ownerUserId,
            bot.Id,
            true,
            actor,
            correlationId,
            cancellationToken);

        return new BotManagementSaveResult(
            bot.Id,
            toggleResult.IsSuccessful,
            true,
            toggleResult.IsEnabled,
            toggleResult.FailureCode,
            toggleResult.FailureReason);
    }

    private async Task<BotManagementSaveResult?> ValidateCommandAsync(
        string ownerUserId,
        Guid? currentBotId,
        BotManagementSaveCommand command,
        CancellationToken cancellationToken)
    {
        var name = command.Name?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "NameRequired", "Bot adı zorunludur.");
        }

        var strategyKey = command.StrategyKey?.Trim();

        if (string.IsNullOrWhiteSpace(strategyKey))
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "StrategyKeyRequired", "Bot için strateji seçimi zorunludur.");
        }

        var strategyExists = await dbContext.TradingStrategies
            .IgnoreQueryFilters()
            .AnyAsync(
                entity => entity.OwnerUserId == ownerUserId &&
                          entity.StrategyKey == strategyKey &&
                          !entity.IsDeleted,
                cancellationToken);

        if (!strategyExists)
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "StrategyNotFound", "Seçilen strateji bulunamadı.");
        }

        var symbol = NormalizeSymbol(command.Symbol);

        if (!IsAllowedSymbol(symbol))
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "PilotSymbolNotAllowed", "Pilot bot yalnizca izinli futures sembolleri ile kaydedilebilir.");
        }

        if (command.Quantity is decimal quantity && quantity <= 0m)
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "QuantityMustBePositive", "Quantity pozitif olmalıdır.");
        }

        var leverage = NormalizeLeverage(command.Leverage);

        if (leverage != 1m)
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "PilotLeverageMustBeOne", "Pilot bot yalnızca 1x leverage ile kaydedilebilir.");
        }

        var marginType = NormalizeMarginType(command.MarginType);

        if (!string.Equals(marginType, FrozenPilotMarginType, StringComparison.Ordinal))
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "PilotMarginTypeMustBeIsolated", "Pilot bot yalnızca ISOLATED margin ile kaydedilebilir.");
        }

        if (command.ExchangeAccountId.HasValue)
        {
            var accountExists = await dbContext.ExchangeAccounts
                .IgnoreQueryFilters()
                .AnyAsync(
                    entity => entity.Id == command.ExchangeAccountId.Value &&
                              entity.OwnerUserId == ownerUserId &&
                              entity.ExchangeName == "Binance" &&
                              !entity.IsDeleted,
                    cancellationToken);

            if (!accountExists)
            {
                return new BotManagementSaveResult(currentBotId, false, false, false, "ExchangeAccountNotFound", "Seçilen Binance hesabı bulunamadı.");
            }
        }

        return null;
    }

    private async Task<BotManagementEditorSnapshot> CreateEditorSnapshotAsync(
        string ownerUserId,
        Guid? botId,
        BotManagementDraftSnapshot draft,
        CancellationToken cancellationToken)
    {
        var strategies = await dbContext.TradingStrategies
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == ownerUserId && !entity.IsDeleted)
            .OrderBy(entity => entity.DisplayName)
            .ThenBy(entity => entity.StrategyKey)
            .ToListAsync(cancellationToken);

        var publishedStrategyIdSet = await LoadRuntimeReadyStrategyIdSetAsync(strategies, cancellationToken);

        var strategyOptions = strategies
            .Select(entity => new BotStrategyOptionSnapshot(
                entity.StrategyKey,
                entity.DisplayName,
                publishedStrategyIdSet.Contains(entity.Id)))
            .ToArray();

        var exchangeAccounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.ExchangeName == "Binance" &&
                !entity.IsDeleted)
            .OrderBy(entity => entity.DisplayName)
            .ThenBy(entity => entity.Id)
            .ToListAsync(cancellationToken);

        var exchangeAccountOptions = exchangeAccounts
            .Select(entity => new BotExchangeAccountOptionSnapshot(
                entity.Id,
                entity.DisplayName,
                entity.CredentialStatus == ExchangeCredentialStatus.Active,
                !entity.IsReadOnly))
            .ToArray();

        return new BotManagementEditorSnapshot(botId, draft, ResolveSymbolOptions(draft.Symbol), strategyOptions, exchangeAccountOptions);
    }

    private async Task<Guid?> ResolveSingleActiveExchangeAccountIdAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        var accountIds = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.ExchangeName == "Binance" &&
                entity.CredentialStatus == ExchangeCredentialStatus.Active &&
                !entity.IsReadOnly &&
                !entity.IsDeleted)
            .OrderBy(entity => entity.Id)
            .Select(entity => entity.Id)
            .ToListAsync(cancellationToken);

        return accountIds.Count == 1
            ? accountIds[0]
            : null;
    }

    private void ResetSchedulerState(Guid botId, bool isEnabled)
    {
        var state = dbContext.BackgroundJobStates
            .IgnoreQueryFilters()
            .SingleOrDefault(entity =>
                entity.BotId == botId &&
                entity.JobType == BackgroundJobTypes.BotExecution &&
                !entity.IsDeleted);

        if (state is null || state.Status == BackgroundJobStatus.Running)
        {
            return;
        }

        if (isEnabled)
        {
            var utcNow = timeProvider.GetUtcNow().UtcDateTime;
            state.Status = BackgroundJobStatus.Pending;
            state.NextRunAtUtc = utcNow;
            state.LastErrorCode = null;
            state.IdempotencyKey = null;
            return;
        }

        state.Status = BackgroundJobStatus.Failed;
        state.NextRunAtUtc = DateTime.MaxValue;
        state.LastErrorCode = "BotDisabled";
        state.IdempotencyKey = null;
    }

    private async Task<HashSet<Guid>> LoadRuntimeReadyStrategyIdSetAsync(
        IReadOnlyCollection<TradingStrategy> strategies,
        CancellationToken cancellationToken)
    {
        var readyIds = new HashSet<Guid>();
        var legacyStrategyIds = strategies
            .Where(entity => !entity.UsesExplicitVersionLifecycle)
            .Select(entity => entity.Id)
            .ToArray();
        var explicitActiveVersionIds = strategies
            .Where(entity => entity.UsesExplicitVersionLifecycle && entity.ActiveTradingStrategyVersionId.HasValue)
            .Select(entity => entity.ActiveTradingStrategyVersionId!.Value)
            .Distinct()
            .ToArray();

        if (legacyStrategyIds.Length > 0)
        {
            var legacyReadyIds = await dbContext.TradingStrategyVersions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    legacyStrategyIds.Contains(entity.TradingStrategyId) &&
                    entity.Status == StrategyVersionStatus.Published &&
                    !entity.IsDeleted)
                .Select(entity => entity.TradingStrategyId)
                .Distinct()
                .ToListAsync(cancellationToken);

            readyIds.UnionWith(legacyReadyIds);
        }

        if (explicitActiveVersionIds.Length > 0)
        {
            var explicitReadyIds = await dbContext.TradingStrategyVersions
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    explicitActiveVersionIds.Contains(entity.Id) &&
                    entity.Status == StrategyVersionStatus.Published &&
                    !entity.IsDeleted)
                .Select(entity => entity.TradingStrategyId)
                .Distinct()
                .ToListAsync(cancellationToken);

            readyIds.UnionWith(explicitReadyIds);
        }

        return readyIds;
    }

    private static decimal? NormalizeQuantity(decimal? quantity)
    {
        return quantity is null
            ? null
            : decimal.Round(quantity.Value, 18, MidpointRounding.AwayFromZero);
    }

    private static decimal NormalizeLeverage(decimal? leverage)
    {
        return leverage ?? 1m;
    }

    private string NormalizeSymbol(string? symbol)
    {
        return string.IsNullOrWhiteSpace(symbol)
            ? optionsValue.DefaultSymbol.Trim().ToUpperInvariant()
            : symbol.Trim().ToUpperInvariant();
    }

    private static string NormalizeMarginType(string? marginType)
    {
        return string.IsNullOrWhiteSpace(marginType)
            ? FrozenPilotMarginType
            : marginType.Trim().ToUpperInvariant();
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }

    private bool IsAllowedSymbol(string symbol)
    {
        return ResolveSymbolOptions(symbol).Contains(symbol, StringComparer.Ordinal);
    }

    private DateTime? ResolveCooldownBlockedUntilUtc(TradingBot bot, ExecutionOrder? latestOrder, DateTime utcNow)
    {
        if (!bot.IsEnabled ||
            latestOrder is null ||
            !latestOrder.CooldownApplied ||
            optionsValue.PerBotCooldownSeconds <= 0)
        {
            return null;
        }

        var blockedUntilUtc = latestOrder.CreatedDate.AddSeconds(optionsValue.PerBotCooldownSeconds);
        return blockedUntilUtc > utcNow
            ? blockedUntilUtc
            : null;
    }

    private static int? ResolveCooldownRemainingSeconds(DateTime? cooldownBlockedUntilUtc, DateTime utcNow)
    {
        if (!cooldownBlockedUntilUtc.HasValue)
        {
            return null;
        }

        return Math.Max(
            0,
            (int)Math.Ceiling((cooldownBlockedUntilUtc.Value - utcNow).TotalSeconds));
    }

    private static LastExecutionDiagnosticSnapshot CreateLastExecutionDiagnosticSnapshot(string? detail)
    {
        var sanitizedDetail = SanitizeExecutionBlockDetail(detail);
        var latencyReasonCode = TryExtractExecutionDiagnosticToken(detail, "LatencyReason", out var latencyReasonValue)
            ? latencyReasonValue
            : null;
        var continuityGapCount = TryExtractExecutionDiagnosticToken(detail, "ContinuityGapCount", out var continuityGapCountValue) &&
            int.TryParse(continuityGapCountValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedContinuityGapCount)
            ? Math.Max(0, parsedContinuityGapCount)
            : (int?)null;
        var lastCandleAtUtc = TryExtractExecutionDiagnosticToken(detail, "LastCandleAtUtc", out var lastCandleAtUtcValue) &&
            DateTime.TryParse(
                lastCandleAtUtcValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsedLastCandleAtUtc)
            ? DateTime.SpecifyKind(parsedLastCandleAtUtc.ToUniversalTime(), DateTimeKind.Utc)
            : (DateTime?)null;
        var dataAgeMilliseconds = TryExtractExecutionDiagnosticToken(detail, "DataAgeMs", out var dataAgeValue) &&
            int.TryParse(dataAgeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDataAgeMilliseconds)
            ? Math.Max(0, parsedDataAgeMilliseconds)
            : (int?)null;
        var affectedSymbol = TryExtractExecutionDiagnosticToken(detail, "Symbol", out var symbolValue)
            ? NormalizeOptionalExecutionDiagnosticValue(symbolValue, toUpperInvariant: true)
            : null;
        var affectedTimeframe = TryExtractExecutionDiagnosticToken(detail, "Timeframe", out var timeframeValue)
            ? NormalizeOptionalExecutionDiagnosticValue(timeframeValue, toUpperInvariant: false)
            : null;

        return new LastExecutionDiagnosticSnapshot(
            sanitizedDetail,
            latencyReasonCode,
            lastCandleAtUtc,
            dataAgeMilliseconds,
            ResolveContinuityState(latencyReasonCode, continuityGapCount, continuityRecoveredAtUtc: null),
            continuityGapCount,
            ResolveStaleReason(latencyReasonCode),
            affectedSymbol,
            affectedTimeframe);
    }

    private static LastExecutionTransitionSnapshot CreateLastExecutionTransitionSnapshot(
        IEnumerable<ExecutionOrderTransition> transitions)
    {
        var transition = transitions.First();

        return new LastExecutionTransitionSnapshot(
            transition.EventCode,
            TruncateClientFacingToken(transition.CorrelationId, 48),
            transitions
                .Select(item => ExtractClientOrderId(item.Detail))
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)),
            transition.OccurredAtUtc,
            CreateLastExecutionDiagnosticSnapshot(transition.Detail));
    }

    private static string? ResolveContinuityState(string? latencyReasonCode, int? continuityGapCount, DateTime? continuityRecoveredAtUtc)
    {
        return ExecutionDecisionDiagnostics.ResolveContinuityState(
            latencyReasonCode,
            continuityGapCount,
            continuityRecoveredAtUtc);
    }

    private static string? ResolveStaleReason(string? latencyReasonCode)
    {
        return ExecutionDecisionDiagnostics.ResolveStaleReason(latencyReasonCode);
    }

    private static bool TryExtractExecutionDiagnosticToken(string? detail, string key, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        var token = key + "=";
        var tokenIndex = detail.IndexOf(token, StringComparison.Ordinal);
        if (tokenIndex < 0)
        {
            return false;
        }

        var valueStartIndex = tokenIndex + token.Length;
        var valueEndIndex = detail.IndexOf(';', valueStartIndex);
        value = (valueEndIndex < 0
                ? detail[valueStartIndex..]
                : detail[valueStartIndex..valueEndIndex])
            .Trim();

        return !string.IsNullOrWhiteSpace(value) &&
            !string.Equals(value, "missing", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeOptionalExecutionDiagnosticValue(string? value, bool toUpperInvariant)
    {
        var normalizedValue = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue) ||
            string.Equals(normalizedValue, "missing", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return toUpperInvariant
            ? normalizedValue.ToUpperInvariant()
            : normalizedValue;
    }

    private static Guid? ResolveDegradedModeStateId(
        ExecutionOrder? order,
        LastExecutionTransitionSnapshot? transitionSnapshot)
    {
        var symbol = transitionSnapshot?.Diagnostics.AffectedSymbol ??
            NormalizeOptionalExecutionDiagnosticValue(order?.Symbol, toUpperInvariant: true);
        var timeframe = transitionSnapshot?.Diagnostics.AffectedTimeframe ??
            NormalizeOptionalExecutionDiagnosticValue(order?.Timeframe, toUpperInvariant: false);

        if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(timeframe))
        {
            return null;
        }

        return DegradedModeDefaults.ResolveStateId(symbol, timeframe);
    }

    private static DateTime? NormalizeUtcNullable(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private static string? SanitizeExecutionBlockDetail(string? detail)
    {
        var normalizedDetail = ExecutionDecisionDiagnostics.ExtractHumanSummary(detail);

        if (string.IsNullOrWhiteSpace(normalizedDetail))
        {
            return null;
        }

        return normalizedDetail.Length <= LastExecutionBlockDetailMaxLength
            ? normalizedDetail
            : normalizedDetail[..LastExecutionBlockDetailMaxLength];
    }

    private static string? ExtractClientOrderId(string? detail)
    {
        if (!TryExtractExecutionDiagnosticToken(detail, "ClientOrderId", out var value))
        {
            return null;
        }

        return TruncateClientFacingToken(value, 64);
    }

    private static string? TruncateClientFacingToken(string? value, int maxLength)
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

    private sealed record LastExecutionDiagnosticSnapshot(
        string? BlockDetail,
        string? LatencyReasonCode,
        DateTime? LastCandleAtUtc,
        int? DataAgeMilliseconds,
        string? ContinuityState,
        int? ContinuityGapCount,
        string? StaleReason,
        string? AffectedSymbol,
        string? AffectedTimeframe);

    private sealed record LastExecutionTransitionSnapshot(
        string EventCode,
        string? CorrelationId,
        string? ClientOrderId,
        DateTime OccurredAtUtc,
        LastExecutionDiagnosticSnapshot Diagnostics);
    private string[] ResolveSymbolOptions(string? currentSymbol = null)
    {
        var symbols = optionsValue.AllowedSymbols
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToUpperInvariant())
            .ToList();

        if (symbols.Count == 0)
        {
            symbols.Add(NormalizeSymbol(optionsValue.DefaultSymbol));
        }

        var normalizedCurrentSymbol = string.IsNullOrWhiteSpace(currentSymbol)
            ? null
            : currentSymbol.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(normalizedCurrentSymbol) &&
            !symbols.Contains(normalizedCurrentSymbol, StringComparer.Ordinal))
        {
            symbols.Add(normalizedCurrentSymbol);
        }

        return symbols
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }
}





