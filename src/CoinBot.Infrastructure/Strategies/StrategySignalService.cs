using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Ai;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.Features;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Ai;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategySignalService(
    ApplicationDbContext dbContext,
    IStrategyEvaluatorService evaluator,
    IRiskPolicyEvaluator riskPolicyEvaluator,
    ITraceService traceService,
    ICorrelationContextAccessor correlationContextAccessor,
    IAiSignalEvaluator aiSignalEvaluator,
    IOptions<AiSignalOptions> aiSignalOptions,
    TimeProvider timeProvider,
    ILogger<StrategySignalService> logger) : IStrategySignalService
{
    private const int ExplainabilitySchemaVersion = 1;
    private const int RepeatedNoOpenPositionExitTraceSuppressionWindowSeconds = 120;
    private const int DecisionSummaryMaxLength = 512;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly AiSignalOptions aiSignalOptionsValue = aiSignalOptions.Value;

    public async Task<StrategySignalGenerationResult> GenerateAsync(
        GenerateStrategySignalsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.EvaluationContext);
        using var signalActivity = CoinBotActivity.StartActivity("CoinBot.Signal.Generate");
        var decisionStopwatch = Stopwatch.StartNew();
        signalActivity.SetTag("coinbot.signal.strategy_version_id", request.TradingStrategyVersionId.ToString());

        var version = await dbContext.TradingStrategyVersions
            .SingleOrDefaultAsync(
                entity => entity.Id == request.TradingStrategyVersionId && !entity.IsDeleted,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Trading strategy version '{request.TradingStrategyVersionId}' was not found.");

        if (version.Status != StrategyVersionStatus.Published)
        {
            throw new InvalidOperationException("Only published strategy versions can generate runtime signals.");
        }

        var strategy = await dbContext.TradingStrategies
            .SingleOrDefaultAsync(
                entity => entity.Id == version.TradingStrategyId && !entity.IsDeleted,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Trading strategy '{version.TradingStrategyId}' was not found.");

        if (strategy.UsesExplicitVersionLifecycle &&
            strategy.ActiveTradingStrategyVersionId != version.Id)
        {
            throw new InvalidOperationException("Only the active strategy version can generate runtime signals.");
        }

        var normalizedContext = NormalizeContext(request.EvaluationContext);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        signalActivity.SetTag("coinbot.signal.strategy_id", strategy.Id.ToString());
        signalActivity.SetTag("coinbot.signal.environment", normalizedContext.Mode.ToString());
        signalActivity.SetTag("coinbot.signal.symbol", normalizedContext.IndicatorSnapshot.Symbol);
        signalActivity.SetTag("coinbot.signal.timeframe", normalizedContext.IndicatorSnapshot.Timeframe);
        signalActivity.SetTag("coinbot.signal.version_is_active", strategy.ActiveTradingStrategyVersionId == version.Id || !strategy.UsesExplicitVersionLifecycle);
        var evaluationReport = evaluator.EvaluateReport(new StrategyEvaluationReportRequest(
            version.TradingStrategyId,
            version.Id,
            version.VersionNumber,
            strategy.StrategyKey,
            strategy.DisplayName,
            version.DefinitionJson,
            normalizedContext,
            now));
        var evaluationResult = evaluationReport.RuleEvaluation;
        var candidateSignalTypes = GetCandidateSignalTypes(evaluationResult);
        var exitCandidateSuppressedByPositionAwareness =
            candidateSignalTypes.Contains(StrategySignalType.Exit) &&
            ShouldSuppressExitCandidateForMissingOpenPosition(request.FeatureSnapshot);
        if (exitCandidateSuppressedByPositionAwareness)
        {
            candidateSignalTypes = FilterCandidateSignalTypesForPositionAwareness(candidateSignalTypes, StrategySignalType.Exit);
            logger.LogInformation(
                "Strategy exit candidate was suppressed because no open position exists for StrategyVersionId {StrategyVersionId}, Symbol {Symbol}, Timeframe {Timeframe}.",
                version.Id,
                normalizedContext.IndicatorSnapshot.Symbol,
                normalizedContext.IndicatorSnapshot.Timeframe);
        }

        var sameDirectionEntrySuppressionSummary = candidateSignalTypes.Contains(StrategySignalType.Entry)
            ? await ResolveSameDirectionEntrySuppressionSummaryAsync(request.FeatureSnapshot, evaluationResult.EntryDirection, cancellationToken)
            : null;
        if (!string.IsNullOrWhiteSpace(sameDirectionEntrySuppressionSummary))
        {
            candidateSignalTypes = FilterCandidateSignalTypesForPositionAwareness(candidateSignalTypes, StrategySignalType.Entry);
            logger.LogInformation(
                "Strategy entry candidate was suppressed because a same-direction open position already exists for StrategyVersionId {StrategyVersionId}, Symbol {Symbol}, Timeframe {Timeframe}.",
                version.Id,
                normalizedContext.IndicatorSnapshot.Symbol,
                normalizedContext.IndicatorSnapshot.Timeframe);
        }

        var aiEvaluations = new List<AiSignalEvaluationResult>(candidateSignalTypes.Count);
        signalActivity.SetTag("coinbot.signal.candidate_count", candidateSignalTypes.Count);

        if (candidateSignalTypes.Count == 0)
        {
            signalActivity.SetTag("coinbot.signal.result", "NoSignals");
            logger.LogInformation(
                "Strategy signal generation produced no persisted signals for StrategyVersionId {StrategyVersionId}.",
                version.Id);

            var noSignalSummary = BuildNoSignalDecisionSummary(
                evaluationReport,
                exitCandidateSuppressedByPositionAwareness,
                sameDirectionEntrySuppressionSummary);

            var noSignalTraceRequest = new DecisionTraceWriteRequest(
                version.OwnerUserId,
                normalizedContext.IndicatorSnapshot.Symbol,
                normalizedContext.IndicatorSnapshot.Timeframe,
                BuildStrategyVersionLabel(version),
                "None",
                "NoSignalCandidate",
                BuildDecisionSnapshotJson(
                    strategy,
                    version,
                    evaluationReport,
                    normalizedContext,
                    evaluationResult,
                    signalType: null,
                    decisionOutcome: "NoSignalCandidate",
                    vetoReasonCode: null,
                    riskScore: null,
                    relatedEntityId: null),
                (int)decisionStopwatch.ElapsedMilliseconds,
                DecisionReasonType: "StrategyCandidate",
                DecisionReasonCode: "NoSignalCandidate",
                DecisionSummary: noSignalSummary,
                DecisionAtUtc: now);

            if (await ShouldWriteNoSignalDecisionTraceAsync(
                    noSignalTraceRequest,
                    exitCandidateSuppressedByPositionAwareness || !string.IsNullOrWhiteSpace(sameDirectionEntrySuppressionSummary),
                    cancellationToken))
            {
                await traceService.WriteDecisionTraceAsync(noSignalTraceRequest, cancellationToken);
            }
            else
            {
                logger.LogDebug(
                    "Repeated no-open-position exit trace suppressed for StrategyVersionId {StrategyVersionId}, Symbol {Symbol}, Timeframe {Timeframe}.",
                    version.Id,
                    normalizedContext.IndicatorSnapshot.Symbol,
                    normalizedContext.IndicatorSnapshot.Timeframe);
            }

            return new StrategySignalGenerationResult(
                evaluationResult,
                Array.Empty<StrategySignalSnapshot>(),
                Array.Empty<StrategySignalVetoSnapshot>(),
                SuppressedDuplicateCount: 0)
            {
                EvaluationReport = evaluationReport,
            AiEvaluations = aiEvaluations.ToArray()
            };
        }

        var persistedSignals = new List<TradingStrategySignal>(candidateSignalTypes.Count);
        var vetoSnapshots = new List<StrategySignalVetoSnapshot>();
        var suppressedDuplicateCount = 0;
        var hasPendingChanges = false;

        foreach (var signalType in candidateSignalTypes)
        {
            var signalScopedEvaluationResult = CreateSignalScopedEvaluationResult(signalType, evaluationResult);
            var duplicateExists = await dbContext.TradingStrategySignals.AnyAsync(
                entity =>
                    entity.TradingStrategyVersionId == version.Id &&
                    entity.SignalType == signalType &&
                    entity.Symbol == normalizedContext.IndicatorSnapshot.Symbol &&
                    entity.Timeframe == normalizedContext.IndicatorSnapshot.Timeframe &&
                    entity.IndicatorCloseTimeUtc == normalizedContext.IndicatorSnapshot.CloseTimeUtc &&
                    !entity.IsDeleted,
                cancellationToken);

            if (duplicateExists)
            {
                suppressedDuplicateCount++;

                logger.LogInformation(
                    "Strategy signal duplicate suppressed for StrategyVersionId {StrategyVersionId}, SignalType {SignalType}, Symbol {Symbol}, Timeframe {Timeframe}, IndicatorCloseTimeUtc {IndicatorCloseTimeUtc:o}.",
                    version.Id,
                    signalType,
                    normalizedContext.IndicatorSnapshot.Symbol,
                    normalizedContext.IndicatorSnapshot.Timeframe,
                    normalizedContext.IndicatorSnapshot.CloseTimeUtc);

                await traceService.WriteDecisionTraceAsync(
                    new DecisionTraceWriteRequest(
                        version.OwnerUserId,
                        normalizedContext.IndicatorSnapshot.Symbol,
                        normalizedContext.IndicatorSnapshot.Timeframe,
                        BuildStrategyVersionLabel(version),
                        signalType.ToString(),
                        "SuppressedDuplicate",
                        BuildDecisionSnapshotJson(
                            strategy,
                            version,
                            evaluationReport,
                            normalizedContext,
                            signalScopedEvaluationResult,
                            signalType,
                            "SuppressedDuplicate",
                            vetoReasonCode: null,
                            riskScore: null,
                            relatedEntityId: null),
                        (int)decisionStopwatch.ElapsedMilliseconds,
                        CorrelationId: ResolveCorrelationId(),
                        RiskScore: null,
                        VetoReasonCode: null,
                        DecisionReasonType: "DuplicateSuppression",
                        DecisionReasonCode: "SuppressedDuplicate",
                        DecisionSummary: "Duplicate execution request was suppressed.",
                        DecisionAtUtc: now),
                    cancellationToken);

                continue;
            }

            AiSignalEvaluationResult? aiEvaluation = null;
            var aiOverlayDecision = AiOverlayDecision.NotApplied;

            if (signalType == StrategySignalType.Entry && aiSignalOptionsValue.Enabled)
            {
                aiEvaluation = await aiSignalEvaluator.EvaluateAsync(
                    new AiSignalEvaluationRequest(
                        request.FeatureSnapshot,
                        normalizedContext.IndicatorSnapshot.Symbol,
                        normalizedContext.IndicatorSnapshot.Timeframe,
                        normalizedContext.Mode,
                        request.FeatureSnapshot?.TradingContext.Plane ?? ExchangeDataPlane.Futures,
                        signalType,
                        strategy.StrategyKey),
                    cancellationToken);
                aiEvaluations.Add(aiEvaluation);

                aiOverlayDecision = ResolveAiOverlayDecision(aiEvaluation, signalType, signalScopedEvaluationResult.Direction, aiSignalOptionsValue);
                if (aiOverlayDecision.IsSuppressed)
                {
                    await traceService.WriteDecisionTraceAsync(
                        new DecisionTraceWriteRequest(
                            version.OwnerUserId,
                            normalizedContext.IndicatorSnapshot.Symbol,
                            normalizedContext.IndicatorSnapshot.Timeframe,
                            BuildStrategyVersionLabel(version),
                            signalType.ToString(),
                            "SuppressedByAi",
                            BuildDecisionSnapshotJson(
                                strategy,
                                version,
                                evaluationReport,
                                normalizedContext,
                                signalScopedEvaluationResult,
                                signalType,
                                "SuppressedByAi",
                                vetoReasonCode: null,
                                riskScore: null,
                                relatedEntityId: null,
                                riskEvaluation: null,
                                aiEvaluation: aiEvaluation),
                            (int)decisionStopwatch.ElapsedMilliseconds,
                            CorrelationId: ResolveCorrelationId(),
                            RiskScore: null,
                            VetoReasonCode: null,
                            DecisionReasonType: aiOverlayDecision.ReasonType,
                            DecisionReasonCode: aiOverlayDecision.ReasonCode,
                            DecisionSummary: aiOverlayDecision.Summary,
                            DecisionAtUtc: now),
                        cancellationToken);

                    continue;
                }
            }

            var riskEvaluation = await riskPolicyEvaluator.EvaluateAsync(
                new RiskPolicyEvaluationRequest(
                    version.OwnerUserId,
                    strategy.Id,
                    version.Id,
                    signalType,
                    normalizedContext.Mode,
                    normalizedContext.IndicatorSnapshot.Symbol,
                    normalizedContext.IndicatorSnapshot.Timeframe),
                cancellationToken);
            var confidenceSnapshot = CreateConfidenceSnapshot(signalType, signalScopedEvaluationResult, riskEvaluation, aiEvaluation, aiOverlayDecision);

            if (riskEvaluation.IsVetoed)
            {
                var veto = await dbContext.TradingStrategySignalVetoes
                    .SingleOrDefaultAsync(
                        entity =>
                            entity.TradingStrategyVersionId == version.Id &&
                            entity.SignalType == signalType &&
                            entity.Symbol == normalizedContext.IndicatorSnapshot.Symbol &&
                            entity.Timeframe == normalizedContext.IndicatorSnapshot.Timeframe &&
                            entity.IndicatorCloseTimeUtc == normalizedContext.IndicatorSnapshot.CloseTimeUtc &&
                            entity.ReasonCode == riskEvaluation.ReasonCode &&
                            !entity.IsDeleted,
                        cancellationToken);

                if (veto is null)
                {
                    veto = CreateVetoEntity(strategy, version, signalType, normalizedContext, riskEvaluation, confidenceSnapshot);
                    dbContext.TradingStrategySignalVetoes.Add(veto);
                    hasPendingChanges = true;
                }

                vetoSnapshots.Add(ToVetoSnapshot(veto, confidenceSnapshot));

                logger.LogInformation(
                    "Strategy signal vetoed for StrategyVersionId {StrategyVersionId}, SignalType {SignalType}, Symbol {Symbol}, Timeframe {Timeframe}, ReasonCode {ReasonCode}.",
                    version.Id,
                    signalType,
                    normalizedContext.IndicatorSnapshot.Symbol,
                    normalizedContext.IndicatorSnapshot.Timeframe,
                    riskEvaluation.ReasonCode);

                await traceService.WriteDecisionTraceAsync(
                    new DecisionTraceWriteRequest(
                        version.OwnerUserId,
                        normalizedContext.IndicatorSnapshot.Symbol,
                        normalizedContext.IndicatorSnapshot.Timeframe,
                        BuildStrategyVersionLabel(version),
                        signalType.ToString(),
                        "Vetoed",
                        BuildDecisionSnapshotJson(
                            strategy,
                            version,
                            evaluationReport,
                            normalizedContext,
                            signalScopedEvaluationResult,
                            signalType,
                            "Vetoed",
                            riskEvaluation.ReasonCode.ToString(),
                            confidenceSnapshot.ScorePercentage,
                            veto.Id,
                            riskEvaluation,
                            aiEvaluation),
                        (int)decisionStopwatch.ElapsedMilliseconds,
                        CorrelationId: ResolveCorrelationId(),
                        RiskScore: confidenceSnapshot.ScorePercentage,
                        VetoReasonCode: riskEvaluation.ReasonCode.ToString(),
                        DecisionReasonType: "RiskVeto",
                        DecisionReasonCode: riskEvaluation.ReasonCode.ToString(),
                        DecisionSummary: ResolveRiskDecisionSummary(riskEvaluation),
                        DecisionAtUtc: now),
                    cancellationToken);

                continue;
            }

            var signal = CreateSignalEntity(
                strategy,
                version,
                signalType,
                normalizedContext,
                signalScopedEvaluationResult,
                confidenceSnapshot,
                riskEvaluation.Snapshot.EvaluatedAtUtc);
            dbContext.TradingStrategySignals.Add(signal);
            persistedSignals.Add(signal);
            hasPendingChanges = true;

            await traceService.WriteDecisionTraceAsync(
                new DecisionTraceWriteRequest(
                    version.OwnerUserId,
                    normalizedContext.IndicatorSnapshot.Symbol,
                    normalizedContext.IndicatorSnapshot.Timeframe,
                    BuildStrategyVersionLabel(version),
                    signalType.ToString(),
                    "Persisted",
                    BuildDecisionSnapshotJson(
                        strategy,
                        version,
                        evaluationReport,
                        normalizedContext,
                        evaluationResult,
                        signalType,
                        "Persisted",
                        vetoReasonCode: null,
                        riskScore: confidenceSnapshot.ScorePercentage,
                        relatedEntityId: signal.Id,
                        riskEvaluation,
                        aiEvaluation),
                    (int)decisionStopwatch.ElapsedMilliseconds,
                    CorrelationId: ResolveCorrelationId(),
                    RiskScore: confidenceSnapshot.ScorePercentage,
                    StrategySignalId: signal.Id,
                    DecisionReasonType: "StrategyCandidate",
                    DecisionReasonCode: "CandidatePersisted",
                    DecisionSummary: "Strategy persisted a candidate signal. Runtime execution gating is pending.",
                    DecisionAtUtc: now),
                cancellationToken);
        }

        if (hasPendingChanges)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        signalActivity.SetTag("coinbot.signal.persisted_count", persistedSignals.Count);
        signalActivity.SetTag("coinbot.signal.veto_count", vetoSnapshots.Count);
        signalActivity.SetTag("coinbot.signal.suppressed_duplicate_count", suppressedDuplicateCount);
        signalActivity.SetTag(
            "coinbot.signal.result",
            persistedSignals.Count > 0
                ? "Persisted"
                : vetoSnapshots.Count > 0
                    ? "Vetoed"
                    : suppressedDuplicateCount > 0
                        ? "Suppressed"
                        : "NoSignals");

        var snapshots = persistedSignals
            .Select(signal => ToSnapshot(
                signal,
                normalizedContext.IndicatorSnapshot,
                DeserializeRequired<StrategyEvaluationResult>(signal.RuleResultSnapshotJson),
                DeserializeConfidenceSnapshot(signal.RiskEvaluationJson)))
            .ToArray();

        foreach (var signal in snapshots)
        {
            logger.LogInformation(
                "Strategy signal {SignalType} persisted for StrategyVersionId {StrategyVersionId} on {Symbol} {Timeframe}.",
                signal.SignalType,
                signal.TradingStrategyVersionId,
                signal.Symbol,
                signal.Timeframe);
        }

        return new StrategySignalGenerationResult(
            evaluationResult,
            snapshots,
            vetoSnapshots,
            suppressedDuplicateCount)
        {
            EvaluationReport = evaluationReport,
            AiEvaluations = aiEvaluations.ToArray()
        };
    }

    public async Task<StrategySignalSnapshot?> GetAsync(
        Guid strategySignalId,
        CancellationToken cancellationToken = default)
    {
        var signal = await dbContext.TradingStrategySignals
            .SingleOrDefaultAsync(entity => entity.Id == strategySignalId && !entity.IsDeleted, cancellationToken);

        if (signal is null)
        {
            return null;
        }

        var indicatorSnapshot = DeserializeRequired<StrategyIndicatorSnapshot>(signal.IndicatorSnapshotJson);
        var evaluationResult = DeserializeRequired<StrategyEvaluationResult>(signal.RuleResultSnapshotJson);
        var confidenceSnapshot = DeserializeConfidenceSnapshot(signal.RiskEvaluationJson);

        return ToSnapshot(signal, indicatorSnapshot, evaluationResult, confidenceSnapshot);
    }

    public async Task<StrategySignalVetoSnapshot?> GetVetoAsync(
        Guid strategySignalVetoId,
        CancellationToken cancellationToken = default)
    {
        var veto = await dbContext.TradingStrategySignalVetoes
            .SingleOrDefaultAsync(entity => entity.Id == strategySignalVetoId && !entity.IsDeleted, cancellationToken);

        if (veto is null)
        {
            return null;
        }

        var confidenceSnapshot = DeserializeRequiredConfidenceSnapshot(veto.RiskEvaluationJson);

        return ToVetoSnapshot(veto, confidenceSnapshot);
    }

    private static StrategyEvaluationContext NormalizeContext(StrategyEvaluationContext context)
    {
        var normalizedIndicatorSnapshot = context.IndicatorSnapshot with
        {
            Symbol = MarketDataSymbolNormalizer.Normalize(context.IndicatorSnapshot.Symbol),
            Timeframe = NormalizeTimeframe(context.IndicatorSnapshot.Timeframe)
        };

        return context with
        {
            IndicatorSnapshot = normalizedIndicatorSnapshot
        };
    }

    private static IReadOnlyCollection<StrategySignalType> GetCandidateSignalTypes(StrategyEvaluationResult evaluationResult)
    {
        if (!evaluationResult.RiskPassed)
        {
            return Array.Empty<StrategySignalType>();
        }

        var signalTypes = new List<StrategySignalType>(capacity: 2);

        if (evaluationResult.EntryMatched && IsActionableDirection(evaluationResult.EntryDirection))
        {
            signalTypes.Add(StrategySignalType.Entry);
        }

        if (evaluationResult.ExitMatched && IsActionableDirection(evaluationResult.ExitDirection))
        {
            signalTypes.Add(StrategySignalType.Exit);
        }

        return signalTypes;
    }

    private static string BuildNoSignalDecisionSummary(
        StrategyEvaluationReportSnapshot evaluationReport,
        bool exitCandidateSuppressedByPositionAwareness,
        string? sameDirectionEntrySuppressionSummary)
    {
        if (!string.IsNullOrWhiteSpace(sameDirectionEntrySuppressionSummary))
        {
            return sameDirectionEntrySuppressionSummary;
        }

        if (exitCandidateSuppressedByPositionAwareness)
        {
            return "Strategy exit candidate was suppressed because no open position exists. Runtime exit persistence was skipped.";
        }

        var explainabilitySummary = string.IsNullOrWhiteSpace(evaluationReport.ExplainabilitySummary)
            ? null
            : evaluationReport.ExplainabilitySummary.Trim();

        var summary = string.IsNullOrWhiteSpace(explainabilitySummary)
            ? "Strategy did not produce an executable candidate."
            : $"Strategy did not produce an executable candidate. Explainability: {explainabilitySummary}";

        return Truncate(summary, DecisionSummaryMaxLength) ?? "Strategy did not produce an executable candidate.";
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength];
    }

    private async Task<bool> ShouldWriteNoSignalDecisionTraceAsync(
        DecisionTraceWriteRequest traceRequest,
        bool suppressionDeduplicationEnabled,
        CancellationToken cancellationToken)
    {
        if (!suppressionDeduplicationEnabled)
        {
            return true;
        }

        var latestTrace = await dbContext.DecisionTraces
            .Where(entity =>
                !entity.IsDeleted &&
                entity.UserId == traceRequest.UserId &&
                entity.Symbol == traceRequest.Symbol &&
                entity.Timeframe == traceRequest.Timeframe &&
                entity.StrategyVersion == traceRequest.StrategyVersion &&
                entity.SignalType == traceRequest.SignalType &&
                entity.DecisionOutcome == traceRequest.DecisionOutcome &&
                entity.DecisionReasonCode == traceRequest.DecisionReasonCode)
            .OrderByDescending(entity => entity.DecisionAtUtc ?? entity.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestTrace is null)
        {
            return true;
        }

        var latestObservedAtUtc = latestTrace.DecisionAtUtc ?? latestTrace.CreatedAtUtc;
        var currentObservedAtUtc = traceRequest.DecisionAtUtc ?? traceRequest.CreatedAtUtc ?? timeProvider.GetUtcNow().UtcDateTime;

        return !string.Equals(latestTrace.DecisionSummary, traceRequest.DecisionSummary, StringComparison.Ordinal) ||
               currentObservedAtUtc - latestObservedAtUtc >= TimeSpan.FromSeconds(RepeatedNoOpenPositionExitTraceSuppressionWindowSeconds);
    }

    private static bool ShouldSuppressExitCandidateForMissingOpenPosition(TradingFeatureSnapshotModel? featureSnapshot)
    {
        return featureSnapshot is not null && !featureSnapshot.TradingContext.HasOpenPosition;
    }

    private async Task<string?> ResolveSameDirectionEntrySuppressionSummaryAsync(
        TradingFeatureSnapshotModel? featureSnapshot,
        StrategyTradeDirection entryDirection,
        CancellationToken cancellationToken)
    {
        if (featureSnapshot is null || !IsActionableDirection(entryDirection))
        {
            return null;
        }

        var currentNetQuantity = await ResolveCurrentNetQuantityAsync(featureSnapshot, cancellationToken);
        if (currentNetQuantity == 0m)
        {
            return null;
        }

        var currentPositionDirection = currentNetQuantity > 0m
            ? StrategyTradeDirection.Long
            : StrategyTradeDirection.Short;
        if (currentPositionDirection != entryDirection)
        {
            return null;
        }

        return $"Strategy entry candidate was suppressed because an open {entryDirection.ToString().ToLowerInvariant()} position already exists. Runtime entry persistence was skipped.";
    }

    private async Task<decimal> ResolveCurrentNetQuantityAsync(
        TradingFeatureSnapshotModel featureSnapshot,
        CancellationToken cancellationToken)
    {
        if (featureSnapshot.TradingContext.TradingMode == ExecutionEnvironment.Demo)
        {
            var quantity = await dbContext.DemoPositions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity =>
                    entity.OwnerUserId == featureSnapshot.UserId &&
                    entity.BotId == featureSnapshot.BotId &&
                    entity.Symbol == featureSnapshot.Symbol &&
                    !entity.IsDeleted)
                .Select(entity => (decimal?)entity.Quantity)
                .FirstOrDefaultAsync(cancellationToken);

            return quantity ?? 0m;
        }

        if (featureSnapshot.TradingContext.Plane == ExchangeDataPlane.Spot)
        {
            var latestHoldingQuantity = await dbContext.SpotPortfolioFills
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(entity =>
                    entity.OwnerUserId == featureSnapshot.UserId &&
                    entity.Symbol == featureSnapshot.Symbol &&
                    !entity.IsDeleted &&
                    (!featureSnapshot.ExchangeAccountId.HasValue || entity.ExchangeAccountId == featureSnapshot.ExchangeAccountId.Value))
                .OrderByDescending(entity => entity.OccurredAtUtc)
                .Select(entity => (decimal?)entity.HoldingQuantityAfter)
                .FirstOrDefaultAsync(cancellationToken);

            return latestHoldingQuantity ?? 0m;
        }

        return await LivePositionTruthResolver.ResolveNetQuantityAsync(
            dbContext,
            featureSnapshot.UserId,
            featureSnapshot.TradingContext.Plane,
            featureSnapshot.ExchangeAccountId,
            featureSnapshot.Symbol,
            cancellationToken);
    }

    private static IReadOnlyCollection<StrategySignalType> FilterCandidateSignalTypesForPositionAwareness(
        IReadOnlyCollection<StrategySignalType> candidateSignalTypes,
        StrategySignalType suppressedSignalType)
    {
        if (!candidateSignalTypes.Contains(suppressedSignalType))
        {
            return candidateSignalTypes;
        }

        return candidateSignalTypes
            .Where(signalType => signalType != suppressedSignalType)
            .ToArray();
    }

    private static StrategyEvaluationResult CreateSignalScopedEvaluationResult(
        StrategySignalType signalType,
        StrategyEvaluationResult evaluationResult)
    {
        var resolvedDirection = ResolveSignalDirection(signalType, evaluationResult);
        return evaluationResult with
        {
            Direction = resolvedDirection,
            EntryMatched = signalType == StrategySignalType.Entry && IsActionableDirection(evaluationResult.EntryDirection),
            ExitMatched = signalType == StrategySignalType.Exit && IsActionableDirection(evaluationResult.ExitDirection)
        };
    }

    private static TradingStrategySignal CreateSignalEntity(
        TradingStrategy strategy,
        TradingStrategyVersion version,
        StrategySignalType signalType,
        StrategyEvaluationContext context,
        StrategyEvaluationResult evaluationResult,
        StrategySignalConfidenceSnapshot confidenceSnapshot,
        DateTime generatedAtUtc)
    {
        return new TradingStrategySignal
        {
            Id = Guid.NewGuid(),
            OwnerUserId = version.OwnerUserId,
            TradingStrategyId = strategy.Id,
            TradingStrategyVersionId = version.Id,
            StrategyVersionNumber = version.VersionNumber,
            StrategySchemaVersion = version.SchemaVersion,
            SignalType = signalType,
            ExecutionEnvironment = context.Mode,
            Symbol = context.IndicatorSnapshot.Symbol,
            Timeframe = context.IndicatorSnapshot.Timeframe,
            IndicatorOpenTimeUtc = context.IndicatorSnapshot.OpenTimeUtc,
            IndicatorCloseTimeUtc = context.IndicatorSnapshot.CloseTimeUtc,
            IndicatorReceivedAtUtc = context.IndicatorSnapshot.ReceivedAtUtc,
            GeneratedAtUtc = generatedAtUtc,
            ExplainabilitySchemaVersion = ExplainabilitySchemaVersion,
            IndicatorSnapshotJson = JsonSerializer.Serialize(context.IndicatorSnapshot, SerializerOptions),
            RuleResultSnapshotJson = JsonSerializer.Serialize(evaluationResult, SerializerOptions),
            RiskEvaluationJson = JsonSerializer.Serialize(confidenceSnapshot, SerializerOptions)
        };
    }

    private static TradingStrategySignalVeto CreateVetoEntity(
        TradingStrategy strategy,
        TradingStrategyVersion version,
        StrategySignalType signalType,
        StrategyEvaluationContext context,
        RiskVetoResult riskEvaluation,
        StrategySignalConfidenceSnapshot confidenceSnapshot)
    {
        return new TradingStrategySignalVeto
        {
            Id = Guid.NewGuid(),
            OwnerUserId = version.OwnerUserId,
            TradingStrategyId = strategy.Id,
            TradingStrategyVersionId = version.Id,
            StrategyVersionNumber = version.VersionNumber,
            StrategySchemaVersion = version.SchemaVersion,
            SignalType = signalType,
            ExecutionEnvironment = context.Mode,
            Symbol = context.IndicatorSnapshot.Symbol,
            Timeframe = context.IndicatorSnapshot.Timeframe,
            IndicatorOpenTimeUtc = context.IndicatorSnapshot.OpenTimeUtc,
            IndicatorCloseTimeUtc = context.IndicatorSnapshot.CloseTimeUtc,
            IndicatorReceivedAtUtc = context.IndicatorSnapshot.ReceivedAtUtc,
            EvaluatedAtUtc = riskEvaluation.Snapshot.EvaluatedAtUtc,
            ReasonCode = riskEvaluation.ReasonCode,
            RiskEvaluationJson = JsonSerializer.Serialize(confidenceSnapshot, SerializerOptions)
        };
    }

    private static StrategySignalSnapshot ToSnapshot(
        TradingStrategySignal signal,
        StrategyIndicatorSnapshot indicatorSnapshot,
        StrategyEvaluationResult evaluationResult,
        StrategySignalConfidenceSnapshot? confidenceSnapshot)
    {
        var resolvedConfidenceSnapshot = confidenceSnapshot ?? CreateUnavailableConfidenceSnapshot();

        return new StrategySignalSnapshot(
            signal.Id,
            signal.TradingStrategyId,
            signal.TradingStrategyVersionId,
            signal.StrategyVersionNumber,
            signal.StrategySchemaVersion,
            signal.SignalType,
            signal.ExecutionEnvironment,
            signal.Symbol,
            signal.Timeframe,
            signal.IndicatorOpenTimeUtc,
            signal.IndicatorCloseTimeUtc,
            signal.IndicatorReceivedAtUtc,
            signal.GeneratedAtUtc,
            new StrategySignalExplainabilityPayload(
                signal.ExplainabilitySchemaVersion,
                signal.TradingStrategyId,
                signal.TradingStrategyVersionId,
                signal.StrategyVersionNumber,
                signal.StrategySchemaVersion,
                signal.ExecutionEnvironment,
                indicatorSnapshot,
                evaluationResult,
                resolvedConfidenceSnapshot,
                CreateSignalUiLogSnapshot(
                    signal.SignalType,
                    signal.Symbol,
                    signal.Timeframe,
                    resolvedConfidenceSnapshot,
                    evaluationResult),
                CreateDuplicateSuppressionSnapshot(signal)));
    }

    private static StrategySignalVetoSnapshot ToVetoSnapshot(
        TradingStrategySignalVeto veto,
        StrategySignalConfidenceSnapshot confidenceSnapshot)
    {
        return new StrategySignalVetoSnapshot(
            veto.Id,
            veto.TradingStrategyId,
            veto.TradingStrategyVersionId,
            veto.StrategyVersionNumber,
            veto.StrategySchemaVersion,
            veto.SignalType,
            veto.ExecutionEnvironment,
            veto.Symbol,
            veto.Timeframe,
            veto.IndicatorOpenTimeUtc,
            veto.IndicatorCloseTimeUtc,
            veto.IndicatorReceivedAtUtc,
            veto.EvaluatedAtUtc,
            confidenceSnapshot,
            CreateVetoUiLogSnapshot(veto, confidenceSnapshot));
    }

    private static StrategySignalConfidenceSnapshot CreateConfidenceSnapshot(
        StrategySignalType signalType,
        StrategyEvaluationResult evaluationResult,
        RiskVetoResult riskEvaluation,
        AiSignalEvaluationResult? aiEvaluation,
        AiOverlayDecision aiOverlayDecision)
    {
        var relevantRules = GetRelevantRuleRoots(signalType, evaluationResult)
            .SelectMany(EnumerateLeafResults)
            .ToArray();
        var totalRuleCount = relevantRules.Length;
        var matchedRuleCount = relevantRules.Count(rule => rule.Matched);
        var rawScore = totalRuleCount == 0
            ? riskEvaluation.IsVetoed
                ? 0
                : 100
            : (int)Math.Round((matchedRuleCount * 100m) / totalRuleCount, MidpointRounding.AwayFromZero);
        var score = riskEvaluation.IsVetoed
            ? Math.Min(rawScore, 39)
            : Math.Min(100, rawScore + aiOverlayDecision.BoostPoints);
        var band = ResolveConfidenceBand(score);
        var riskSummary = string.IsNullOrWhiteSpace(riskEvaluation.ReasonSummary)
            ? FormatRiskReason(riskEvaluation.ReasonCode)
            : riskEvaluation.ReasonSummary.Trim();
        var overlaySummary = aiOverlayDecision.IsApplied
            ? $" AI overlay {aiOverlayDecision.Disposition ?? "NotApplied"} ({aiOverlayDecision.ReasonCode})."
            : string.Empty;
        var summary = riskEvaluation.IsVetoed
            ? $"Risk approval failed: {riskSummary}.{overlaySummary}".Trim()
            : $"Risk approval passed: {riskSummary}.{overlaySummary}".Trim();

        return new StrategySignalConfidenceSnapshot(
            score,
            band,
            matchedRuleCount,
            totalRuleCount,
            IsDeterministic: true,
            IsRiskApproved: !riskEvaluation.IsVetoed,
            IsVetoed: riskEvaluation.IsVetoed,
            RiskReasonCode: riskEvaluation.ReasonCode,
            IsVirtualRiskCheck: riskEvaluation.Snapshot.IsVirtualCheck,
            Summary: summary,
            CurrentDailyLossPercentage: riskEvaluation.Snapshot.CurrentDailyLossPercentage,
            MaxDailyLossPercentage: riskEvaluation.Snapshot.MaxDailyLossPercentage,
            CurrentWeeklyLossPercentage: riskEvaluation.Snapshot.CurrentWeeklyLossPercentage,
            MaxWeeklyLossPercentage: riskEvaluation.Snapshot.MaxWeeklyLossPercentage,
            CurrentLeverage: riskEvaluation.Snapshot.CurrentLeverage,
            ProjectedLeverage: riskEvaluation.Snapshot.ProjectedLeverage,
            MaxLeverage: riskEvaluation.Snapshot.MaxLeverage,
            CurrentSymbolExposurePercentage: riskEvaluation.Snapshot.CurrentSymbolExposurePercentage,
            ProjectedSymbolExposurePercentage: riskEvaluation.Snapshot.ProjectedSymbolExposurePercentage,
            MaxSymbolExposurePercentage: riskEvaluation.Snapshot.MaxSymbolExposurePercentage,
            CurrentOpenPositionCount: riskEvaluation.Snapshot.OpenPositionCount,
            ProjectedOpenPositionCount: riskEvaluation.Snapshot.ProjectedOpenPositionCount,
            MaxConcurrentPositions: riskEvaluation.Snapshot.MaxConcurrentPositions,
            RiskBaseAsset: riskEvaluation.Snapshot.BaseAsset,
            CurrentCoinExposurePercentage: riskEvaluation.Snapshot.CurrentCoinExposurePercentage,
            ProjectedCoinExposurePercentage: riskEvaluation.Snapshot.ProjectedCoinExposurePercentage,
            MaxCoinExposurePercentage: riskEvaluation.Snapshot.MaxCoinExposurePercentage,
            RiskScopeSummary: riskSummary,
            AiEvaluation: aiEvaluation,
            AiOverlayDisposition: aiOverlayDecision.Disposition,
            AiOverlayBoostPoints: aiOverlayDecision.BoostPoints);
    }

    private static StrategySignalConfidenceSnapshot CreateLegacyConfidenceSnapshot(RiskVetoResult riskEvaluation)
    {
        var score = riskEvaluation.IsVetoed
            ? 39
            : 100;

        var riskSummary = string.IsNullOrWhiteSpace(riskEvaluation.ReasonSummary)
            ? FormatRiskReason(riskEvaluation.ReasonCode)
            : riskEvaluation.ReasonSummary.Trim();

        return new StrategySignalConfidenceSnapshot(
            score,
            ResolveConfidenceBand(score),
            MatchedRuleCount: 0,
            TotalRuleCount: 0,
            IsDeterministic: true,
            IsRiskApproved: !riskEvaluation.IsVetoed,
            IsVetoed: riskEvaluation.IsVetoed,
            RiskReasonCode: riskEvaluation.ReasonCode,
            IsVirtualRiskCheck: riskEvaluation.Snapshot.IsVirtualCheck,
            Summary: riskEvaluation.IsVetoed
                ? $"Legacy risk evaluation vetoed the signal: {riskSummary}"
                : $"Legacy risk evaluation approved the signal: {riskSummary}",
            CurrentDailyLossPercentage: riskEvaluation.Snapshot.CurrentDailyLossPercentage,
            MaxDailyLossPercentage: riskEvaluation.Snapshot.MaxDailyLossPercentage,
            CurrentWeeklyLossPercentage: riskEvaluation.Snapshot.CurrentWeeklyLossPercentage,
            MaxWeeklyLossPercentage: riskEvaluation.Snapshot.MaxWeeklyLossPercentage,
            CurrentLeverage: riskEvaluation.Snapshot.CurrentLeverage,
            ProjectedLeverage: riskEvaluation.Snapshot.ProjectedLeverage,
            MaxLeverage: riskEvaluation.Snapshot.MaxLeverage,
            CurrentSymbolExposurePercentage: riskEvaluation.Snapshot.CurrentSymbolExposurePercentage,
            ProjectedSymbolExposurePercentage: riskEvaluation.Snapshot.ProjectedSymbolExposurePercentage,
            MaxSymbolExposurePercentage: riskEvaluation.Snapshot.MaxSymbolExposurePercentage,
            CurrentOpenPositionCount: riskEvaluation.Snapshot.OpenPositionCount,
            ProjectedOpenPositionCount: riskEvaluation.Snapshot.ProjectedOpenPositionCount,
            MaxConcurrentPositions: riskEvaluation.Snapshot.MaxConcurrentPositions,
            RiskBaseAsset: riskEvaluation.Snapshot.BaseAsset,
            CurrentCoinExposurePercentage: riskEvaluation.Snapshot.CurrentCoinExposurePercentage,
            ProjectedCoinExposurePercentage: riskEvaluation.Snapshot.ProjectedCoinExposurePercentage,
            MaxCoinExposurePercentage: riskEvaluation.Snapshot.MaxCoinExposurePercentage,
            RiskScopeSummary: riskSummary,
            AiEvaluation: null);
    }

    private static StrategySignalConfidenceSnapshot CreateUnavailableConfidenceSnapshot()
    {
        return new StrategySignalConfidenceSnapshot(
            ScorePercentage: 0,
            Band: StrategySignalConfidenceBand.Low,
            MatchedRuleCount: 0,
            TotalRuleCount: 0,
            IsDeterministic: true,
            IsRiskApproved: false,
            IsVetoed: false,
            RiskReasonCode: RiskVetoReasonCode.None,
            IsVirtualRiskCheck: false,
            Summary: "Confidence snapshot unavailable.",
            AiEvaluation: null);
    }

    private static AiOverlayDecision ResolveAiOverlayDecision(
        AiSignalEvaluationResult aiEvaluation,
        StrategySignalType signalType,
        StrategyTradeDirection strategyDirection,
        AiSignalOptions aiSignalOptions)
    {
        if (aiEvaluation.IsFallback)
        {
            return AiOverlayDecision.Suppress(
                "AiFallback",
                $"Ai{aiEvaluation.FallbackReason ?? AiSignalFallbackReason.EvaluationException}",
                aiEvaluation.ReasonSummary);
        }

        if (aiEvaluation.SignalDirection == AiSignalDirection.Neutral)
        {
            return AiOverlayDecision.Suppress("AiOverlay", "AiNeutral", "AI overlay returned a neutral signal.");
        }

        if (signalType == StrategySignalType.Entry &&
            !DoesAiDirectionMatchStrategyDirection(aiEvaluation.SignalDirection, strategyDirection))
        {
            return AiOverlayDecision.Suppress(
                "AiOverlay",
                "AiDirectionMismatch",
                $"AI overlay returned {aiEvaluation.SignalDirection}, but the strategy entry direction is {strategyDirection}.");
        }

        if (aiEvaluation.SignalDirection == AiSignalDirection.Long && !aiSignalOptions.AllowLong)
        {
            return AiOverlayDecision.Suppress("AiOverlay", "AiDirectionNotAllowed", "AI overlay long decisions are disabled by configuration.");
        }

        if (aiEvaluation.SignalDirection == AiSignalDirection.Short && !aiSignalOptions.AllowShort)
        {
            return AiOverlayDecision.Suppress("AiOverlay", "AiDirectionNotAllowed", "AI overlay short decisions are disabled by configuration.");
        }

        if (aiEvaluation.ConfidenceScore < aiSignalOptions.MinimumConfidence)
        {
            return AiOverlayDecision.Suppress(
                "AiOverlay",
                "AiLowConfidence",
                $"AI overlay confidence {aiEvaluation.ConfidenceScore:0.##} is below the minimum {aiSignalOptions.MinimumConfidence:0.##}.");
        }

        var boostThreshold = Math.Max(aiSignalOptions.MinimumConfidence, 0.90m);
        if (aiEvaluation.ConfidenceScore >= boostThreshold)
        {
            return AiOverlayDecision.Allow(
                disposition: "Boost",
                boostPoints: 5,
                reasonType: "AiOverlay",
                reasonCode: "AiBoost",
                summary: "AI overlay strongly confirmed the strategy direction.");
        }

        return AiOverlayDecision.Allow(
            disposition: "Confirm",
            boostPoints: 0,
            reasonType: "AiOverlay",
            reasonCode: "AiConfirm",
            summary: "AI overlay confirmed the strategy direction.");
    }

    private static StrategySignalConfidenceBand ResolveConfidenceBand(int scorePercentage)
    {
        return scorePercentage switch
        {
            >= 85 => StrategySignalConfidenceBand.High,
            >= 60 => StrategySignalConfidenceBand.Medium,
            _ => StrategySignalConfidenceBand.Low
        };
    }

    private static StrategySignalLogExplainabilitySnapshot CreateSignalUiLogSnapshot(
        StrategySignalType signalType,
        string symbol,
        string timeframe,
        StrategySignalConfidenceSnapshot confidenceSnapshot,
        StrategyEvaluationResult evaluationResult)
    {
        var ruleDrivers = GetRelevantRuleRoots(signalType, evaluationResult)
            .SelectMany(EnumerateLeafResults)
            .Where(rule => rule.Matched)
            .Select(FormatRuleDriver)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        var aiDrivers = CreateAiDrivers(confidenceSnapshot.AiEvaluation);

        var drivers = confidenceSnapshot.IsVetoed
            ? (new[] { $"Risk gate: {FormatRiskReason(confidenceSnapshot.RiskReasonCode)}" })
                .Concat(ruleDrivers)
                .Concat(aiDrivers)
                .Take(3)
                .ToArray()
            : ruleDrivers
                .Concat(aiDrivers)
                .Take(3)
                .ToArray();
        var directionLabel = FormatSignalDirectionLabel(ResolveSignalDirection(signalType, evaluationResult));
        var title = confidenceSnapshot.IsVetoed
            ? $"{signalType} ({directionLabel}) signal vetoed"
            : $"{signalType} ({directionLabel}) signal created";
        var summary = confidenceSnapshot.IsVetoed
            ? $"{symbol} {timeframe} {signalType.ToString().ToLowerInvariant()} ({directionLabel.ToLowerInvariant()}) signal vetoed because {FormatRiskReason(confidenceSnapshot.RiskReasonCode)}."
            : $"{symbol} {timeframe} {signalType.ToString().ToLowerInvariant()} ({directionLabel.ToLowerInvariant()}) signal created from matching strategy rules.";
        var aiSuffix = BuildAiSummarySuffix(confidenceSnapshot.AiEvaluation);

        return new StrategySignalLogExplainabilitySnapshot(
            title,
            string.IsNullOrWhiteSpace(aiSuffix) ? summary : $"{summary} {aiSuffix}",
            drivers,
            [
                symbol,
                timeframe,
                signalType.ToString(),
                $"Direction {directionLabel}",
                $"Confidence {confidenceSnapshot.ScorePercentage}%",
                confidenceSnapshot.IsVetoed
                    ? $"Veto {confidenceSnapshot.RiskReasonCode}"
                    : confidenceSnapshot.Band.ToString(),
                ..CreateAiTags(confidenceSnapshot.AiEvaluation)
            ]);
    }

    private static StrategySignalLogExplainabilitySnapshot CreateVetoUiLogSnapshot(
        TradingStrategySignalVeto veto,
        StrategySignalConfidenceSnapshot confidenceSnapshot)
    {
        return new StrategySignalLogExplainabilitySnapshot(
            $"{veto.SignalType} signal vetoed",
            string.IsNullOrWhiteSpace(BuildAiSummarySuffix(confidenceSnapshot.AiEvaluation))
                ? $"{veto.Symbol} {veto.Timeframe} {veto.SignalType.ToString().ToLowerInvariant()} signal vetoed because {FormatRiskReason(confidenceSnapshot.RiskReasonCode)}."
                : $"{veto.Symbol} {veto.Timeframe} {veto.SignalType.ToString().ToLowerInvariant()} signal vetoed because {FormatRiskReason(confidenceSnapshot.RiskReasonCode)}. {BuildAiSummarySuffix(confidenceSnapshot.AiEvaluation)}",
            [
                $"Risk gate: {FormatRiskReason(confidenceSnapshot.RiskReasonCode)}",
                confidenceSnapshot.Summary,
                ..CreateAiDrivers(confidenceSnapshot.AiEvaluation)
            ],
            [
                veto.Symbol,
                veto.Timeframe,
                veto.SignalType.ToString(),
                $"Confidence {confidenceSnapshot.ScorePercentage}%",
                $"Veto {confidenceSnapshot.RiskReasonCode}",
                ..CreateAiTags(confidenceSnapshot.AiEvaluation)
            ]);
    }

    private static IReadOnlyCollection<string> CreateAiDrivers(AiSignalEvaluationResult? aiEvaluation)
    {
        if (aiEvaluation is null)
        {
            return Array.Empty<string>();
        }

        return
        [
            $"AI {aiEvaluation.ProviderName}: {aiEvaluation.SignalDirection} {aiEvaluation.ConfidenceScore:0.##}",
            aiEvaluation.ReasonSummary
        ];
    }

    private static IReadOnlyCollection<string> CreateAiTags(AiSignalEvaluationResult? aiEvaluation)
    {
        if (aiEvaluation is null)
        {
            return Array.Empty<string>();
        }

        return
        [
            $"AI {aiEvaluation.ProviderName}",
            $"AI {aiEvaluation.SignalDirection}",
            aiEvaluation.IsFallback
                ? $"AI Fallback {aiEvaluation.FallbackReason}"
                : $"AI Confidence {aiEvaluation.ConfidenceScore:0.##}"
        ];
    }

    private static string? BuildAiSummarySuffix(AiSignalEvaluationResult? aiEvaluation)
    {
        return aiEvaluation is null
            ? null
            : $"AI overlay: {aiEvaluation.SignalDirection} from {aiEvaluation.ProviderName} at {aiEvaluation.ConfidenceScore:0.##}. {aiEvaluation.ReasonSummary}";
    }


    private static StrategyTradeDirection ResolveSignalDirection(
        StrategySignalType signalType,
        StrategyEvaluationResult evaluationResult)
    {
        return signalType switch
        {
            StrategySignalType.Entry => evaluationResult.EntryDirection,
            StrategySignalType.Exit => evaluationResult.ExitDirection,
            _ => evaluationResult.Direction
        };
    }

    private static bool IsActionableDirection(StrategyTradeDirection direction)
    {
        return direction is StrategyTradeDirection.Long or StrategyTradeDirection.Short;
    }

    private static bool DoesAiDirectionMatchStrategyDirection(
        AiSignalDirection aiDirection,
        StrategyTradeDirection strategyDirection)
    {
        return strategyDirection switch
        {
            StrategyTradeDirection.Long => aiDirection == AiSignalDirection.Long,
            StrategyTradeDirection.Short => aiDirection == AiSignalDirection.Short,
            _ => false
        };
    }

    private static string FormatSignalDirectionLabel(StrategyTradeDirection direction)
    {
        return direction switch
        {
            StrategyTradeDirection.Long => "Long",
            StrategyTradeDirection.Short => "Short",
            _ => "Neutral"
        };
    }

    private static StrategySignalDuplicateSuppressionSnapshot CreateDuplicateSuppressionSnapshot(TradingStrategySignal signal)
    {
        return new StrategySignalDuplicateSuppressionSnapshot(
            Enabled: true,
            WasSuppressed: false,
            Fingerprint: CreateDuplicateFingerprint(
                signal.TradingStrategyVersionId,
                signal.SignalType,
                signal.Symbol,
                signal.Timeframe,
                signal.IndicatorCloseTimeUtc));
    }

    private static string CreateDuplicateFingerprint(
        Guid tradingStrategyVersionId,
        StrategySignalType signalType,
        string symbol,
        string timeframe,
        DateTime indicatorCloseTimeUtc)
    {
        return $"{tradingStrategyVersionId:N}:{signalType}:{symbol}:{timeframe}:{indicatorCloseTimeUtc:O}";
    }

    private static IReadOnlyCollection<StrategyRuleResultSnapshot> GetRelevantRuleRoots(
        StrategySignalType signalType,
        StrategyEvaluationResult evaluationResult)
    {
        var rules = new List<StrategyRuleResultSnapshot>(capacity: 2);

        switch (signalType)
        {
            case StrategySignalType.Entry when evaluationResult.EntryRuleResult is not null:
                rules.Add(evaluationResult.EntryRuleResult);
                break;
            case StrategySignalType.Exit when evaluationResult.ExitRuleResult is not null:
                rules.Add(evaluationResult.ExitRuleResult);
                break;
        }

        if (evaluationResult.RiskRuleResult is not null)
        {
            rules.Add(evaluationResult.RiskRuleResult);
        }

        return rules;
    }

    private static IEnumerable<StrategyRuleResultSnapshot> EnumerateLeafResults(StrategyRuleResultSnapshot root)
    {
        if (root.Children.Count == 0)
        {
            yield return root;
            yield break;
        }

        foreach (var child in root.Children)
        {
            foreach (var leaf in EnumerateLeafResults(child))
            {
                yield return leaf;
            }
        }
    }

    private static string FormatRuleDriver(StrategyRuleResultSnapshot rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Path))
        {
            return "Grouped rule matched.";
        }

        var leftLabel = FormatPathLabel(rule.Path);
        var comparison = FormatComparison(rule.Comparison);
        var leftValue = rule.LeftValue ?? "?";

        if (rule.OperandKind == StrategyRuleOperandKind.Path && !string.IsNullOrWhiteSpace(rule.Operand))
        {
            return $"{leftLabel} {leftValue} {comparison} {FormatPathLabel(rule.Operand)} {rule.RightValue ?? "?"}";
        }

        return $"{leftLabel} {leftValue} {comparison} {rule.RightValue ?? rule.Operand ?? "?"}";
    }

    private static string FormatPathLabel(string path)
    {
        return path.Trim().ToLowerInvariant() switch
        {
            "context.mode" => "Mode",
            "indicator.samplecount" => "Sample Count",
            "indicator.requiredsamplecount" => "Required Sample Count",
            "indicator.state" => "Indicator State",
            "indicator.rsi.value" => "RSI",
            "indicator.rsi.isready" => "RSI Ready",
            "indicator.macd.macdline" => "MACD Line",
            "indicator.macd.signalline" => "MACD Signal",
            "indicator.macd.histogram" => "MACD Histogram",
            "indicator.macd.isready" => "MACD Ready",
            "indicator.bollinger.middleband" => "Bollinger Mid",
            "indicator.bollinger.upperband" => "Bollinger Upper",
            "indicator.bollinger.lowerband" => "Bollinger Lower",
            "indicator.bollinger.isready" => "Bollinger Ready",
            _ => path
        };
    }

    private static string FormatComparison(StrategyRuleComparisonOperator? comparison)
    {
        return comparison switch
        {
            StrategyRuleComparisonOperator.Equals => "=",
            StrategyRuleComparisonOperator.NotEquals => "!=",
            StrategyRuleComparisonOperator.GreaterThan => ">",
            StrategyRuleComparisonOperator.GreaterThanOrEqual => ">=",
            StrategyRuleComparisonOperator.LessThan => "<",
            StrategyRuleComparisonOperator.LessThanOrEqual => "<=",
            _ => "?"
        };
    }

    private static string FormatRiskReason(RiskVetoReasonCode reasonCode)
    {
        return reasonCode switch
        {
            RiskVetoReasonCode.None => "risk gate passed",
            RiskVetoReasonCode.RiskProfileMissing => "risk profile missing",
            RiskVetoReasonCode.KillSwitchEnabled => "kill switch enabled",
            RiskVetoReasonCode.AccountEquityUnavailable => "account equity unavailable",
            RiskVetoReasonCode.DailyLossLimitBreached => "daily loss limit breached",
            RiskVetoReasonCode.ExposureLimitBreached => "exposure limit breached",
            RiskVetoReasonCode.LeverageLimitBreached => "leverage limit breached",
            RiskVetoReasonCode.WeeklyLossLimitBreached => "weekly loss limit breached",
            RiskVetoReasonCode.SymbolExposureLimitBreached => "symbol exposure limit breached",
            RiskVetoReasonCode.MaxConcurrentPositionsBreached => "max concurrent positions limit breached",
            RiskVetoReasonCode.CoinSpecificLimitBreached => "coin-specific limit breached",
            RiskVetoReasonCode.RiskProfileConfigurationInvalid => "risk profile configuration invalid",
            _ => reasonCode.ToString()
        };
    }

    private static string NormalizeTimeframe(string timeframe)
    {
        var normalizedTimeframe = timeframe?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedTimeframe))
        {
            throw new ArgumentException("The timeframe is required.", nameof(timeframe));
        }

        return normalizedTimeframe;
    }

    private static T DeserializeRequired<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json, SerializerOptions);

        return value is not null
            ? value
            : throw new InvalidOperationException($"Strategy signal JSON payload could not be deserialized as '{typeof(T).Name}'.");
    }

    private static T? DeserializeOptional<T>(string? json)
    {
        return string.IsNullOrWhiteSpace(json)
            ? default
            : JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }

    private static StrategySignalConfidenceSnapshot? DeserializeConfidenceSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StrategySignalConfidenceSnapshot>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            var legacyRiskEvaluation = DeserializeOptional<RiskVetoResult>(json);
            return legacyRiskEvaluation is null
                ? null
                : CreateLegacyConfidenceSnapshot(legacyRiskEvaluation);
        }
    }

    private static StrategySignalConfidenceSnapshot DeserializeRequiredConfidenceSnapshot(string json)
    {
        return DeserializeConfidenceSnapshot(json)
            ?? throw new InvalidOperationException("Strategy signal confidence payload could not be deserialized.");
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }

    private static string ResolveRiskDecisionSummary(RiskVetoResult riskEvaluation)
    {
        var normalizedSummary = riskEvaluation.ReasonSummary?.Trim();
        return string.IsNullOrWhiteSpace(normalizedSummary)
            ? "Risk veto blocked execution."
            : normalizedSummary;
    }

    private sealed record AiOverlayDecision(bool IsSuppressed, bool IsApplied, string? Disposition, int BoostPoints, string ReasonType, string ReasonCode, string Summary)
    {
        public static AiOverlayDecision NotApplied { get; } = new(false, false, null, 0, string.Empty, string.Empty, string.Empty);

        public static AiOverlayDecision Suppress(string reasonType, string reasonCode, string summary) =>
            new(true, true, "Suppress", 0, reasonType, reasonCode, summary);

        public static AiOverlayDecision Allow(string disposition, int boostPoints, string reasonType, string reasonCode, string summary) =>
            new(false, true, disposition, boostPoints, reasonType, reasonCode, summary);
    }

    private string ResolveCorrelationId()
    {
        var scopedCorrelationId = correlationContextAccessor.Current?.CorrelationId;

        return string.IsNullOrWhiteSpace(scopedCorrelationId)
            ? Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N")
            : scopedCorrelationId.Trim();
    }

    private static string BuildStrategyVersionLabel(TradingStrategyVersion version)
    {
        return $"StrategyVersion:{version.Id:N}:v{version.VersionNumber}:s{version.SchemaVersion}";
    }

    private string BuildDecisionSnapshotJson(
        TradingStrategy strategy,
        TradingStrategyVersion version,
        StrategyEvaluationReportSnapshot report,
        StrategyEvaluationContext context,
        StrategyEvaluationResult evaluationResult,
        StrategySignalType? signalType,
        string decisionOutcome,
        string? vetoReasonCode,
        int? riskScore,
        Guid? relatedEntityId,
        RiskVetoResult? riskEvaluation = null,
        AiSignalEvaluationResult? aiEvaluation = null)
    {
        return JsonSerializer.Serialize(
            new
            {
                TradingStrategyId = strategy.Id,
                strategy.StrategyKey,
                strategy.DisplayName,
                TradingStrategyVersionId = version.Id,
                StrategyVersionNumber = version.VersionNumber,
                StrategySchemaVersion = version.SchemaVersion,
                StrategyVersionStatus = version.Status.ToString(),
                strategy.UsesExplicitVersionLifecycle,
                strategy.ActiveTradingStrategyVersionId,
                IsActiveVersion = strategy.ActiveTradingStrategyVersionId == version.Id || !strategy.UsesExplicitVersionLifecycle,
                report.TemplateKey,
                report.TemplateName,
                report.TemplateRevisionNumber,
                report.TemplateSource,
                report.Outcome,
                report.AggregateScore,
                report.PassedRuleCount,
                report.FailedRuleCount,
                report.ExplainabilitySummary,
                PassedRules = report.PassedRules.Take(3).ToArray(),
                FailedRules = report.FailedRules.Take(3).ToArray(),
                ExecutionEnvironment = context.Mode.ToString(),
                context.IndicatorSnapshot.Symbol,
                context.IndicatorSnapshot.Timeframe,
                context.IndicatorSnapshot.CloseTimeUtc,
                context.IndicatorSnapshot.ReceivedAtUtc,
                SignalType = signalType?.ToString() ?? "None",
                evaluationResult.EntryMatched,
                evaluationResult.ExitMatched,
                evaluationResult.RiskPassed,
                Direction = evaluationResult.Direction.ToString(),
                EntryDirection = evaluationResult.EntryDirection.ToString(),
                ExitDirection = evaluationResult.ExitDirection.ToString(),
                RiskOutcome = riskEvaluation is null
                    ? null
                    : riskEvaluation.IsVetoed
                        ? "Vetoed"
                        : "Allowed",
                RiskReasonCode = riskEvaluation?.ReasonCode.ToString(),
                RiskSummary = riskEvaluation?.ReasonSummary,
                RiskEvaluatedAtUtc = riskEvaluation?.Snapshot.EvaluatedAtUtc,
                AiEvaluation = aiEvaluation is null
                    ? null
                    : new
                    {
                        Direction = aiEvaluation.SignalDirection.ToString(),
                        aiEvaluation.ConfidenceScore,
                        aiEvaluation.ReasonSummary,
                        aiEvaluation.FeatureSnapshotId,
                        aiEvaluation.ProviderName,
                        aiEvaluation.ProviderModel,
                        aiEvaluation.LatencyMs,
                        aiEvaluation.IsFallback,
                        FallbackReason = aiEvaluation.FallbackReason?.ToString(),
                        aiEvaluation.RawResponseCaptured,
                        aiEvaluation.EvaluatedAtUtc
                    },
                DecisionOutcome = decisionOutcome,
                VetoReasonCode = vetoReasonCode,
                RiskScore = riskScore,
                RelatedEntityId = relatedEntityId,
                CapturedAtUtc = timeProvider.GetUtcNow().UtcDateTime
            },
            SerializerOptions);
    }
}














