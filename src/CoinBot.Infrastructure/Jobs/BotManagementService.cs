using System.Collections.Generic;
using System.Globalization;
using CoinBot.Application.Abstractions.Bots;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Contracts.Common;
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
    UserOperationsStreamHub? userOperationsStreamHub = null,
    IStrategyTemplateCatalogService? strategyTemplateCatalogService = null) : IBotManagementService
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
        List<TradingFeatureSnapshot> latestFeatureSnapshots = botIds.Length == 0
            ? []
            : await dbContext.TradingFeatureSnapshots
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
        var relevantStrategyIds = strategies
            .Select(entity => entity.Id)
            .Distinct()
            .ToArray();
        var relevantSymbols = bots
            .Select(entity => NormalizeSymbol(entity.Symbol))
            .Where(entity => !string.IsNullOrWhiteSpace(entity))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var strategySignals = relevantStrategyIds.Length == 0 || relevantSymbols.Length == 0
            ? []
            : await dbContext.TradingStrategySignals
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    !entity.IsDeleted &&
                    relevantStrategyIds.Contains(entity.TradingStrategyId) &&
                    relevantSymbols.Contains(entity.Symbol))
                .OrderByDescending(entity => entity.GeneratedAtUtc)
                .ThenByDescending(entity => entity.CreatedDate)
                .ToListAsync(cancellationToken);
        var strategySignalIds = strategySignals
            .Select(entity => entity.Id)
            .ToArray();
        var decisionTracesBySignalId = strategySignalIds.Length == 0
            ? new Dictionary<Guid, DecisionTrace[]>()
            : (await dbContext.DecisionTraces
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(entity =>
                    !entity.IsDeleted &&
                    entity.StrategySignalId.HasValue &&
                    strategySignalIds.Contains(entity.StrategySignalId.Value))
                .OrderByDescending(entity => entity.DecisionAtUtc ?? entity.CreatedAtUtc)
                .ThenByDescending(entity => entity.CreatedAtUtc)
                .ToListAsync(cancellationToken))
            .GroupBy(entity => entity.StrategySignalId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());
        var strategySignalsByKey = strategySignals
            .GroupBy(entity => BuildStrategySignalScopeKey(entity.TradingStrategyId, entity.Symbol))
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
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
        var ordersBySignalId = latestOrders
            .GroupBy(entity => entity.StrategySignalId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entity => entity.CreatedDate).ToArray());
        var featureSnapshotsByBotId = latestFeatureSnapshots
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
                featureSnapshotsByBotId.TryGetValue(bot.Id, out var featureSnapshot);
                var currentOrder = IsExecutionOrderCurrentForFeatureSnapshot(order, featureSnapshot) ? order : null;
                var cooldownBlockedUntilUtc = ResolveCooldownBlockedUntilUtc(bot, currentOrder, utcNow);
                var cooldownRemainingSeconds = ResolveCooldownRemainingSeconds(cooldownBlockedUntilUtc, utcNow);
                LastExecutionTransitionSnapshot? lastExecutionTransition = null;

                if (currentOrder?.State is ExecutionOrderState.Rejected or ExecutionOrderState.Failed)
                {
                    latestTransitionSnapshotsByOrderId.TryGetValue(currentOrder.Id, out lastExecutionTransition);
                }
                else if (currentOrder is not null)
                {
                    latestTransitionSnapshotsByOrderId.TryGetValue(currentOrder.Id, out lastExecutionTransition);
                }

                var degradedModeStateId = ResolveDegradedModeStateId(currentOrder, lastExecutionTransition);
                var degradedModeState = degradedModeStateId.HasValue &&
                    degradedModeStatesById.TryGetValue(degradedModeStateId.Value, out var resolvedDegradedModeState)
                    ? resolvedDegradedModeState
                    : null;
                var latencyReasonCode = lastExecutionTransition?.Diagnostics.LatencyReasonCode ??
                    degradedModeState?.ReasonCode.ToString();
                var decisionAtUtc = lastExecutionTransition?.OccurredAtUtc ?? currentOrder?.UpdatedDate ?? featureSnapshot?.EvaluatedAtUtc;
                var isBlockedDecision = currentOrder?.State is ExecutionOrderState.Rejected or ExecutionOrderState.Failed;
                var decisionReasonCode = currentOrder is null
                    ? featureSnapshot?.LastDecisionCode
                    : ExecutionDecisionDiagnostics.ResolveDecisionReasonCode(
                        isBlockedDecision,
                        currentOrder.FailureCode ?? lastExecutionTransition?.EventCode,
                        latencyReasonCode);
                var decisionReasonType = decisionReasonCode is null
                    ? null
                    : ExecutionDecisionDiagnostics.ResolveDecisionReasonType(
                        decisionReasonCode,
                        latencyReasonCode,
                        strategyDecisionOutcome: featureSnapshot?.LastExecutionState ?? featureSnapshot?.LastDecisionOutcome);
                var decisionSummary = decisionReasonCode is null
                    ? null
                    : ExecutionDecisionDiagnostics.ResolveDecisionSummary(
                        isBlockedDecision || currentOrder is null,
                        decisionReasonType ?? "Other",
                        decisionReasonCode,
                        lastExecutionTransition?.Diagnostics.BlockDetail ?? currentOrder?.FailureDetail);
                var lastExecutionLastCandleAtUtc = lastExecutionTransition?.Diagnostics.LastCandleAtUtc ??
                    NormalizeUtcNullable(degradedModeState?.LatestDataTimestampAtUtc) ??
                    NormalizeUtcNullable(featureSnapshot?.MarketDataTimestampUtc);
                var lastExecutionDataAgeMilliseconds = lastExecutionTransition?.Diagnostics.DataAgeMilliseconds ??
                    ExecutionDecisionDiagnostics.ResolveDecisionDataAgeMilliseconds(
                        degradedModeState,
                        decisionAtUtc,
                        currentOrder?.FailureDetail);
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
                var runtimeDirection = ResolveRuntimeDirection(order ?? currentOrder);
                var longRegimeGate = BuildLongRegimeGateSnapshot(
                    bot,
                    featureSnapshot);
                var strategySignalsForBot = strategy is not null &&
                    strategySignalsByKey.TryGetValue(BuildStrategySignalScopeKey(strategy.Id, bot.Symbol), out var resolvedSignals)
                    ? resolvedSignals
                    : [];
                var latestSignal = strategySignalsForBot.FirstOrDefault();
                var latestSignalDecision = latestSignal is not null && decisionTracesBySignalId.TryGetValue(latestSignal.Id, out var latestSignalDecisions)
                    ? latestSignalDecisions.FirstOrDefault()
                    : null;
                var latestSignalOrder = latestSignal is not null && ordersBySignalId.TryGetValue(latestSignal.Id, out var latestSignalOrders)
                    ? latestSignalOrders.FirstOrDefault(entity => entity.BotId == bot.Id) ?? latestSignalOrders.FirstOrDefault()
                    : null;
                var signalLifecycleMetrics = BuildSignalLifecycleMetrics(
                    strategySignalsForBot,
                    decisionTracesBySignalId,
                    latestOrders,
                    bot.Id);

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
                    currentOrder?.State.ToString() ?? featureSnapshot?.LastExecutionState,
                    currentOrder?.FailureCode ?? featureSnapshot?.LastFailureCode,
                    currentOrder?.State is ExecutionOrderState.Rejected or ExecutionOrderState.Failed
                        ? lastExecutionTransition?.Diagnostics.BlockDetail
                        : null,
                    currentOrder?.RejectionStage.ToString(),
                    currentOrder?.SubmittedToBroker ?? false,
                    currentOrder?.RetryEligible ?? false,
                    currentOrder?.CooldownApplied ?? false,
                    currentOrder?.ReduceOnly ?? false,
                    currentOrder?.StopLossPrice.HasValue ?? false,
                    currentOrder?.TakeProfitPrice.HasValue ?? false,
                    currentOrder?.DuplicateSuppressed ?? false,
                    lastExecutionTransition?.EventCode,
                    NormalizeOptionalExecutionDiagnosticValue(
                        lastExecutionTransition?.CorrelationId,
                        toUpperInvariant: false),
                    lastExecutionTransition?.ClientOrderId,
                    cooldownBlockedUntilUtc,
                    cooldownRemainingSeconds,
                    currentOrder?.UpdatedDate ?? featureSnapshot?.UpdatedDate,
                    bot.UpdatedDate,
                    lastExecutionLastCandleAtUtc,
                    lastExecutionDataAgeMilliseconds,
                    lastExecutionContinuityState,
                    lastExecutionContinuityGapCount,
                    lastExecutionStaleReason,
                    lastExecutionTransition?.Diagnostics.AffectedSymbol,
                    lastExecutionTransition?.Diagnostics.AffectedTimeframe,
                    currentOrder is null ? featureSnapshot?.LastDecisionOutcome : ExecutionDecisionDiagnostics.ResolveDecisionOutcome(isBlockedDecision),
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
                    NormalizeUtcNullable(shadowDecision?.EvaluatedAtUtc),
                    runtimeDirection.Label,
                    runtimeDirection.Tone,
                    runtimeDirection.Summary,
                    bot.DirectionMode,
                    longRegimeGate.Label,
                    longRegimeGate.Tone,
                    longRegimeGate.PolicySummary,
                    longRegimeGate.LiveSummary,
                    longRegimeGate.ExplainSummary,
                    latestSignal?.SignalType.ToString(),
                    NormalizeUtcNullable(latestSignal?.GeneratedAtUtc),
                    latestSignalDecision?.DecisionOutcome,
                    latestSignalDecision?.DecisionReasonCode,
                    latestSignalDecision?.DecisionSummary,
                    NormalizeUtcNullable(latestSignalDecision?.DecisionAtUtc) ?? NormalizeUtcNullable(latestSignalDecision?.CreatedAtUtc),
                    latestSignalOrder?.State.ToString(),
                    latestSignalOrder?.FailureCode ?? latestSignalDecision?.DecisionReasonCode,
                    signalLifecycleMetrics.EntryGeneratedCount,
                    signalLifecycleMetrics.EntrySkippedCount,
                    signalLifecycleMetrics.EntryVetoedCount,
                    signalLifecycleMetrics.EntryOrderedCount,
                    signalLifecycleMetrics.EntryFilledCount,
                    signalLifecycleMetrics.ExitGeneratedCount,
                    signalLifecycleMetrics.ExitSkippedCount,
                    signalLifecycleMetrics.ExitVetoedCount,
                    signalLifecycleMetrics.ExitOrderedCount,
                    signalLifecycleMetrics.ExitFilledCount);
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
            IsEnabled: false,
            DirectionMode: TradingBotDirectionMode.LongOnly);

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
            bot.IsEnabled,
            bot.DirectionMode);

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

        var resolvedStrategyKey = await ResolveOrMaterializeStrategyKeyAsync(ownerUserId, command.StrategyKey, cancellationToken);
        if (resolvedStrategyKey is null)
        {
            return new BotManagementSaveResult(null, false, false, false, "StrategyNotFound", "Seçilen strateji bulunamadı.");
        }

        var bot = new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = NormalizeRequired(command.Name, nameof(command.Name)),
            StrategyKey = resolvedStrategyKey,
            Symbol = NormalizeSymbol(command.Symbol),
            Quantity = NormalizeQuantity(command.Quantity),
            ExchangeAccountId = command.ExchangeAccountId,
            Leverage = NormalizeLeverage(command.Leverage),
            MarginType = NormalizeMarginType(command.MarginType),
            DirectionMode = NormalizeDirectionMode(command.DirectionMode),
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

        var resolvedStrategyKey = await ResolveOrMaterializeStrategyKeyAsync(ownerUserId, command.StrategyKey, cancellationToken);
        if (resolvedStrategyKey is null)
        {
            return new BotManagementSaveResult(botId, false, false, bot.IsEnabled, "StrategyNotFound", "Seçilen strateji bulunamadı.");
        }

        var wasEnabled = bot.IsEnabled;
        bot.Name = NormalizeRequired(command.Name, nameof(command.Name));
        bot.StrategyKey = resolvedStrategyKey;
        bot.Symbol = NormalizeSymbol(command.Symbol);
        bot.Quantity = NormalizeQuantity(command.Quantity);
        bot.ExchangeAccountId = command.ExchangeAccountId;
        bot.Leverage = NormalizeLeverage(command.Leverage);
        bot.MarginType = NormalizeMarginType(command.MarginType);
        bot.DirectionMode = NormalizeDirectionMode(command.DirectionMode);

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

        var strategyExists = await StrategySelectionExistsAsync(ownerUserId, strategyKey, cancellationToken);

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
        var sharedTemplateOptions = await LoadSharedTemplateStrategyOptionsAsync(ownerUserId, strategies, cancellationToken);

        var strategyOptions = strategies
            .Select(entity => new BotStrategyOptionSnapshot(
                entity.StrategyKey,
                entity.DisplayName,
                publishedStrategyIdSet.Contains(entity.Id)))
            .Concat(sharedTemplateOptions)
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

    private async Task<bool> StrategySelectionExistsAsync(
        string ownerUserId,
        string strategyKey,
        CancellationToken cancellationToken)
    {
        var ownStrategyExists = await dbContext.TradingStrategies
            .IgnoreQueryFilters()
            .AnyAsync(
                entity => entity.OwnerUserId == ownerUserId &&
                          entity.StrategyKey == strategyKey &&
                          !entity.IsDeleted,
                cancellationToken);
        if (ownStrategyExists)
        {
            return true;
        }

        return await FindAccessibleTemplateAsync(strategyKey, cancellationToken) is not null;
    }

    private async Task<string?> ResolveOrMaterializeStrategyKeyAsync(
        string ownerUserId,
        string strategyKey,
        CancellationToken cancellationToken)
    {
        var normalizedStrategyKey = NormalizeRequired(strategyKey, nameof(strategyKey));
        var ownStrategy = await dbContext.TradingStrategies
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.OwnerUserId == ownerUserId &&
                          entity.StrategyKey == normalizedStrategyKey &&
                          !entity.IsDeleted,
                cancellationToken);
        if (ownStrategy is not null)
        {
            return ownStrategy.StrategyKey;
        }

        var template = await FindAccessibleTemplateAsync(normalizedStrategyKey, cancellationToken);
        if (template is null)
        {
            return null;
        }

        var utcNow = timeProvider.GetUtcNow().UtcDateTime;
        var strategy = new TradingStrategy
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            StrategyKey = template.TemplateKey,
            DisplayName = template.TemplateName,
            UsesExplicitVersionLifecycle = true,
            ActiveTradingStrategyVersionId = null,
            ActiveVersionActivatedAtUtc = null,
            CreatedDate = utcNow,
            UpdatedDate = utcNow
        };

        dbContext.TradingStrategies.Add(strategy);
        await dbContext.SaveChangesAsync(cancellationToken);

        var version = new TradingStrategyVersion
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            TradingStrategyId = strategy.Id,
            SchemaVersion = template.SchemaVersion,
            VersionNumber = 1,
            Status = StrategyVersionStatus.Published,
            DefinitionJson = template.DefinitionJson,
            PublishedAtUtc = utcNow,
            CreatedDate = utcNow,
            UpdatedDate = utcNow
        };

        dbContext.TradingStrategyVersions.Add(version);
        await dbContext.SaveChangesAsync(cancellationToken);

        strategy.ActiveTradingStrategyVersionId = version.Id;
        strategy.ActiveVersionActivatedAtUtc = utcNow;
        strategy.UpdatedDate = utcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return strategy.StrategyKey;
    }

    private async Task<IReadOnlyCollection<BotStrategyOptionSnapshot>> LoadSharedTemplateStrategyOptionsAsync(
        string ownerUserId,
        IReadOnlyCollection<TradingStrategy> ownerStrategies,
        CancellationToken cancellationToken)
    {
        if (strategyTemplateCatalogService is null)
        {
            return Array.Empty<BotStrategyOptionSnapshot>();
        }

        var ownerStrategyKeys = ownerStrategies
            .Select(entity => entity.StrategyKey)
            .ToHashSet(StringComparer.Ordinal);
        var ownerTemplateKeys = await dbContext.TradingStrategyTemplates
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == ownerUserId && !entity.IsDeleted)
            .Select(entity => entity.TemplateKey)
            .ToListAsync(cancellationToken);
        var ownerTemplateKeySet = ownerTemplateKeys.ToHashSet(StringComparer.Ordinal);
        try
        {
            var platformAdminOwnerIds = await dbContext.UserClaims
                .AsNoTracking()
                .Where(claim =>
                    claim.ClaimType == ApplicationClaimTypes.Permission &&
                    claim.ClaimValue == ApplicationPermissions.PlatformAdministration)
                .Select(claim => claim.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (platformAdminOwnerIds.Count == 0)
            {
                return Array.Empty<BotStrategyOptionSnapshot>();
            }

            return await dbContext.TradingStrategyTemplates
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(template =>
                    platformAdminOwnerIds.Contains(template.OwnerUserId) &&
                    template.IsActive &&
                    !template.IsDeleted &&
                    template.PublishedTradingStrategyTemplateRevisionId.HasValue &&
                    !ownerStrategyKeys.Contains(template.TemplateKey) &&
                    !ownerTemplateKeySet.Contains(template.TemplateKey))
                .Join(
                    dbContext.TradingStrategyTemplateRevisions
                        .AsNoTracking()
                        .IgnoreQueryFilters()
                        .Where(revision =>
                            !revision.IsDeleted &&
                            revision.ValidationStatusCode == "Valid"),
                    template => template.PublishedTradingStrategyTemplateRevisionId,
                    revision => (Guid?)revision.Id,
                    (template, _) => new { template.TemplateKey, template.TemplateName })
                .OrderBy(template => template.TemplateName)
                .ThenBy(template => template.TemplateKey)
                .Select(template => new BotStrategyOptionSnapshot(
                    template.TemplateKey,
                    template.TemplateName + " · Shared",
                    true))
                .ToArrayAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Array.Empty<BotStrategyOptionSnapshot>();
        }
    }

    private async Task<StrategyTemplateSnapshot?> FindAccessibleTemplateAsync(
        string templateKey,
        CancellationToken cancellationToken)
    {
        if (strategyTemplateCatalogService is null)
        {
            return null;
        }

        try
        {
            var template = await strategyTemplateCatalogService.GetAsync(templateKey, cancellationToken);
            return template.IsActive &&
                   template.PublishedRevisionNumber > 0 &&
                   template.Validation.IsValid
                ? template
                : null;
        }
        catch (Exception exception) when (exception is StrategyTemplateCatalogException or StrategyDefinitionValidationException or StrategyRuleParseException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string BuildStrategySignalScopeKey(Guid tradingStrategyId, string? symbol)
    {
        return $"{tradingStrategyId:N}|{NormalizeSignalScopeSymbol(symbol)}";
    }

    private static string NormalizeSignalScopeSymbol(string? symbol)
    {
        return string.IsNullOrWhiteSpace(symbol)
            ? string.Empty
            : symbol.Trim().ToUpperInvariant();
    }

    private static BotSignalLifecycleMetrics BuildSignalLifecycleMetrics(
        IReadOnlyCollection<TradingStrategySignal> signals,
        IReadOnlyDictionary<Guid, DecisionTrace[]> decisionTracesBySignalId,
        IReadOnlyCollection<ExecutionOrder> executionOrders,
        Guid botId)
    {
        var entryGeneratedCount = signals.Count(entity => entity.SignalType == StrategySignalType.Entry);
        var exitGeneratedCount = signals.Count(entity => entity.SignalType == StrategySignalType.Exit);

        var entrySkippedCount = 0;
        var entryVetoedCount = 0;
        var exitSkippedCount = 0;
        var exitVetoedCount = 0;

        foreach (var signal in signals)
        {
            var latestDecision = decisionTracesBySignalId.TryGetValue(signal.Id, out var traces)
                ? traces.FirstOrDefault()
                : null;

            if (latestDecision is null)
            {
                continue;
            }

            if (signal.SignalType == StrategySignalType.Entry)
            {
                if (IsSkippedDecisionOutcome(latestDecision.DecisionOutcome))
                {
                    entrySkippedCount++;
                }
                else if (IsVetoedDecisionOutcome(latestDecision.DecisionOutcome))
                {
                    entryVetoedCount++;
                }
            }
            else if (signal.SignalType == StrategySignalType.Exit)
            {
                if (IsSkippedDecisionOutcome(latestDecision.DecisionOutcome))
                {
                    exitSkippedCount++;
                }
                else if (IsVetoedDecisionOutcome(latestDecision.DecisionOutcome))
                {
                    exitVetoedCount++;
                }
            }
        }

        var ordersForBot = executionOrders
            .Where(entity => entity.BotId == botId)
            .ToArray();

        var entryOrderedCount = ordersForBot
            .Where(entity => entity.SignalType == StrategySignalType.Entry)
            .Select(entity => entity.StrategySignalId)
            .Distinct()
            .Count();
        var entryFilledCount = ordersForBot
            .Where(entity => entity.SignalType == StrategySignalType.Entry && entity.State == ExecutionOrderState.Filled)
            .Select(entity => entity.StrategySignalId)
            .Distinct()
            .Count();
        var exitOrderedCount = ordersForBot
            .Where(entity => entity.SignalType == StrategySignalType.Exit)
            .Select(entity => entity.StrategySignalId)
            .Distinct()
            .Count();
        var exitFilledCount = ordersForBot
            .Where(entity => entity.SignalType == StrategySignalType.Exit && entity.State == ExecutionOrderState.Filled)
            .Select(entity => entity.StrategySignalId)
            .Distinct()
            .Count();

        return new BotSignalLifecycleMetrics(
            entryGeneratedCount,
            entrySkippedCount,
            entryVetoedCount,
            entryOrderedCount,
            entryFilledCount,
            exitGeneratedCount,
            exitSkippedCount,
            exitVetoedCount,
            exitOrderedCount,
            exitFilledCount);
    }

    private static bool IsSkippedDecisionOutcome(string? decisionOutcome)
    {
        return string.Equals(decisionOutcome, "Skipped", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVetoedDecisionOutcome(string? decisionOutcome)
    {
        return string.Equals(decisionOutcome, "Blocked", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(decisionOutcome, "Vetoed", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record BotSignalLifecycleMetrics(
        int EntryGeneratedCount,
        int EntrySkippedCount,
        int EntryVetoedCount,
        int EntryOrderedCount,
        int EntryFilledCount,
        int ExitGeneratedCount,
        int ExitSkippedCount,
        int ExitVetoedCount,
        int ExitOrderedCount,
        int ExitFilledCount);

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

    private static TradingBotDirectionMode NormalizeDirectionMode(TradingBotDirectionMode directionMode)
    {
        return Enum.IsDefined(typeof(TradingBotDirectionMode), directionMode)
            ? directionMode
            : TradingBotDirectionMode.LongOnly;
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

    private static (string Label, string Tone, string Summary) ResolveRuntimeDirection(ExecutionOrder? order)
    {
        if (order is null)
        {
            return ("Neutral", "neutral", "Henüz runtime yönü yok.");
        }

        var isClosingTrade = order.ReduceOnly || order.SignalType == StrategySignalType.Exit;
        var direction = order.Side == ExecutionOrderSide.Buy
            ? (isClosingTrade ? "Short" : "Long")
            : (isClosingTrade ? "Long" : "Short");
        var action = isClosingTrade ? "Exit" : "Entry";
        var tone = direction == "Long" ? "success" : "danger";
        return ($"{direction} {action}", tone, $"{direction} {action} • {order.State} • {order.Symbol}");
    }

    private static bool IsExecutionOrderCurrentForFeatureSnapshot(ExecutionOrder? order, TradingFeatureSnapshot? featureSnapshot)
    {
        if (order is null)
        {
            return false;
        }

        if (featureSnapshot is null)
        {
            return true;
        }

        var orderUpdatedAtUtc = NormalizeUtcNullable(order.LastStateChangedAtUtc) ?? NormalizeUtcNullable(order.CreatedDate);
        var featureEvaluatedAtUtc = NormalizeUtcNullable(featureSnapshot.EvaluatedAtUtc) ?? NormalizeUtcNullable(featureSnapshot.UpdatedDate);

        return !featureEvaluatedAtUtc.HasValue ||
            orderUpdatedAtUtc.HasValue && orderUpdatedAtUtc.Value >= featureEvaluatedAtUtc.Value;
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

    private LongRegimeGateSnapshot BuildLongRegimeGateSnapshot(
        TradingBot bot,
        TradingFeatureSnapshot? featureSnapshot)
    {
        if (bot.DirectionMode == TradingBotDirectionMode.ShortOnly)
        {
            return new LongRegimeGateSnapshot(
                "N/A",
                "neutral",
                "Bu bot short-only; long regime gate uygulanmaz.",
                null,
                "Long regime kalibrasyonu bu bot icin devre disi.");
        }

        var policySummary = optionsValue.BuildRegimeThresholdSummary(StrategyTradeDirection.Long);

        if (!optionsValue.IsRegimeAwareEntryDisciplineEnabled(StrategyTradeDirection.Long))
        {
            return new LongRegimeGateSnapshot(
                "FILTER OFF",
                "warning",
                policySummary,
                featureSnapshot is null ? null : BuildLongRegimeLiveSummary(featureSnapshot),
                "Long regime filter policy seviyesinde kapali; runtime long entry bu guard ile bloklanmaz.");
        }

        if (featureSnapshot is null)
        {
            return new LongRegimeGateSnapshot(
                "LIVE DATA YOK",
                "warning",
                policySummary,
                null,
                "Canli RSI / MACD / Bollinger genisligi olmadan long regime karari tek bakista dogrulanamaz.");
        }

        var blockers = EvaluateLongRegimeDrivers(featureSnapshot);
        var liveSummary = BuildLongRegimeLiveSummary(featureSnapshot);

        if (blockers.Count == 0)
        {
            return new LongRegimeGateSnapshot(
                "PASS NOW",
                "success",
                policySummary,
                liveSummary,
                "Mevcut canli degerler long regime threshold'larini geciyor; ayni sinyal runtime order asamasina ilerleyebilir.");
        }

        var explainSummary = string.Join("; ", blockers);

        return new LongRegimeGateSnapshot(
            "BLOCKED NOW",
            "danger",
            policySummary,
            liveSummary,
            explainSummary);
    }

    private List<string> EvaluateLongRegimeDrivers(TradingFeatureSnapshot snapshot)
    {
        var drivers = new List<string>();
        var rsiThreshold = optionsValue.ResolveRegimeMaxEntryRsi(StrategyTradeDirection.Long);
        var macdThreshold = optionsValue.ResolveRegimeMacdThreshold(StrategyTradeDirection.Long);
        var bollingerWidthThreshold = optionsValue.ResolveRegimeMinBollingerWidthPercentage(StrategyTradeDirection.Long);

        if (snapshot.Rsi.HasValue && rsiThreshold > 0m && snapshot.Rsi.Value >= rsiThreshold)
        {
            drivers.Add($"RSI {snapshot.Rsi.Value.ToString("0.##", CultureInfo.InvariantCulture)} >= {rsiThreshold.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        if (snapshot.MacdHistogram.HasValue && snapshot.MacdHistogram.Value < macdThreshold)
        {
            drivers.Add($"MACD histogram {snapshot.MacdHistogram.Value.ToString("0.####", CultureInfo.InvariantCulture)} < {macdThreshold.ToString("0.####", CultureInfo.InvariantCulture)}");
        }

        if (snapshot.BollingerBandWidth.HasValue &&
            bollingerWidthThreshold > 0m &&
            snapshot.BollingerBandWidth.Value < bollingerWidthThreshold)
        {
            drivers.Add($"Bollinger width {snapshot.BollingerBandWidth.Value.ToString("0.####", CultureInfo.InvariantCulture)}% < {bollingerWidthThreshold.ToString("0.####", CultureInfo.InvariantCulture)}%");
        }

        return drivers;
    }

    private static string BuildLongRegimeLiveSummary(TradingFeatureSnapshot snapshot)
    {
        return string.Join(
            " · ",
            $"RSI={FormatLongRegimeMetric(snapshot.Rsi, "0.##")}",
            $"MACD hist={FormatLongRegimeMetric(snapshot.MacdHistogram, "0.####")}",
            $"Bollinger width={FormatLongRegimeMetric(snapshot.BollingerBandWidth, "0.####", suffix: "%")}");
    }

    private static string FormatLongRegimeMetric(decimal? value, string format, string suffix = "")
    {
        return value.HasValue
            ? value.Value.ToString(format, CultureInfo.InvariantCulture) + suffix
            : "n/a";
    }

    private sealed record LongRegimeGateSnapshot(
        string Label,
        string Tone,
        string PolicySummary,
        string? LiveSummary,
        string ExplainSummary);

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





