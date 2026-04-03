using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategySignalService(
    ApplicationDbContext dbContext,
    IStrategyEvaluatorService evaluator,
    IRiskPolicyEvaluator riskPolicyEvaluator,
    ITraceService traceService,
    ICorrelationContextAccessor correlationContextAccessor,
    TimeProvider timeProvider,
    ILogger<StrategySignalService> logger) : IStrategySignalService
{
    private const int ExplainabilitySchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

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

        var strategy = await dbContext.TradingStrategies
            .SingleOrDefaultAsync(
                entity => entity.Id == version.TradingStrategyId && !entity.IsDeleted,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Trading strategy '{version.TradingStrategyId}' was not found.");

        var normalizedContext = NormalizeContext(request.EvaluationContext);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        signalActivity.SetTag("coinbot.signal.strategy_id", strategy.Id.ToString());
        signalActivity.SetTag("coinbot.signal.environment", normalizedContext.Mode.ToString());
        signalActivity.SetTag("coinbot.signal.symbol", normalizedContext.IndicatorSnapshot.Symbol);
        signalActivity.SetTag("coinbot.signal.timeframe", normalizedContext.IndicatorSnapshot.Timeframe);
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
        signalActivity.SetTag("coinbot.signal.candidate_count", candidateSignalTypes.Count);

        if (candidateSignalTypes.Count == 0)
        {
            signalActivity.SetTag("coinbot.signal.result", "NoSignals");
            logger.LogInformation(
                "Strategy signal generation produced no persisted signals for StrategyVersionId {StrategyVersionId}.",
                version.Id);

            await traceService.WriteDecisionTraceAsync(
                new DecisionTraceWriteRequest(
                    version.OwnerUserId,
                    normalizedContext.IndicatorSnapshot.Symbol,
                    normalizedContext.IndicatorSnapshot.Timeframe,
                    BuildStrategyVersionLabel(version),
                    "None",
                    "NoSignalCandidate",
                    BuildDecisionSnapshotJson(
                        version,
                        normalizedContext,
                        evaluationResult,
                        signalType: null,
                        decisionOutcome: "NoSignalCandidate",
                        vetoReasonCode: null,
                        riskScore: null,
                        relatedEntityId: null),
                    (int)decisionStopwatch.ElapsedMilliseconds),
                cancellationToken);

            return new StrategySignalGenerationResult(
                evaluationResult,
                Array.Empty<StrategySignalSnapshot>(),
                Array.Empty<StrategySignalVetoSnapshot>(),
                SuppressedDuplicateCount: 0)
            {
                EvaluationReport = evaluationReport
            };
        }

        var persistedSignals = new List<TradingStrategySignal>(candidateSignalTypes.Count);
        var vetoSnapshots = new List<StrategySignalVetoSnapshot>();
        var suppressedDuplicateCount = 0;
        var hasPendingChanges = false;

        foreach (var signalType in candidateSignalTypes)
        {
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
                            version,
                            normalizedContext,
                            evaluationResult,
                            signalType,
                            "SuppressedDuplicate",
                            vetoReasonCode: null,
                            riskScore: null,
                            relatedEntityId: null),
                        (int)decisionStopwatch.ElapsedMilliseconds,
                        CorrelationId: ResolveCorrelationId(),
                        RiskScore: null,
                        VetoReasonCode: null),
                    cancellationToken);

                continue;
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
            var confidenceSnapshot = CreateConfidenceSnapshot(signalType, evaluationResult, riskEvaluation);

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
                            version,
                            normalizedContext,
                            evaluationResult,
                            signalType,
                            "Vetoed",
                            riskEvaluation.ReasonCode.ToString(),
                            confidenceSnapshot.ScorePercentage,
                            veto.Id),
                        (int)decisionStopwatch.ElapsedMilliseconds,
                        CorrelationId: ResolveCorrelationId(),
                        RiskScore: confidenceSnapshot.ScorePercentage,
                        VetoReasonCode: riskEvaluation.ReasonCode.ToString()),
                    cancellationToken);

                continue;
            }

            var signal = CreateSignalEntity(
                strategy,
                version,
                signalType,
                normalizedContext,
                evaluationResult,
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
                        version,
                        normalizedContext,
                        evaluationResult,
                        signalType,
                        "Persisted",
                        vetoReasonCode: null,
                        riskScore: confidenceSnapshot.ScorePercentage,
                        relatedEntityId: signal.Id),
                    (int)decisionStopwatch.ElapsedMilliseconds,
                    CorrelationId: ResolveCorrelationId(),
                    RiskScore: confidenceSnapshot.ScorePercentage,
                    StrategySignalId: signal.Id),
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
                evaluationResult,
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
            EvaluationReport = evaluationReport
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

        if (evaluationResult.EntryMatched)
        {
            signalTypes.Add(StrategySignalType.Entry);
        }

        if (evaluationResult.ExitMatched)
        {
            signalTypes.Add(StrategySignalType.Exit);
        }

        return signalTypes;
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
        RiskVetoResult riskEvaluation)
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
            : rawScore;
        var band = ResolveConfidenceBand(score);
        var summary = riskEvaluation.IsVetoed
            ? $"Risk approval failed: {FormatRiskReason(riskEvaluation.ReasonCode)}."
            : $"Signal approved with {matchedRuleCount}/{totalRuleCount} matching strategy rules.";

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
            Summary: summary);
    }

    private static StrategySignalConfidenceSnapshot CreateLegacyConfidenceSnapshot(RiskVetoResult riskEvaluation)
    {
        var score = riskEvaluation.IsVetoed
            ? 39
            : 100;

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
                ? $"Legacy risk evaluation vetoed the signal: {FormatRiskReason(riskEvaluation.ReasonCode)}."
                : "Legacy risk evaluation approved the signal.");
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
            Summary: "Confidence snapshot unavailable.");
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

        var drivers = confidenceSnapshot.IsVetoed
            ? (new[] { $"Risk gate: {FormatRiskReason(confidenceSnapshot.RiskReasonCode)}" })
                .Concat(ruleDrivers)
                .Take(3)
                .ToArray()
            : ruleDrivers;
        var title = confidenceSnapshot.IsVetoed
            ? $"{signalType} signal vetoed"
            : $"{signalType} signal created";
        var summary = confidenceSnapshot.IsVetoed
            ? $"{symbol} {timeframe} {signalType.ToString().ToLowerInvariant()} signal vetoed because {FormatRiskReason(confidenceSnapshot.RiskReasonCode)}."
            : $"{symbol} {timeframe} {signalType.ToString().ToLowerInvariant()} signal created from matching strategy rules.";

        return new StrategySignalLogExplainabilitySnapshot(
            title,
            summary,
            drivers,
            [
                symbol,
                timeframe,
                signalType.ToString(),
                $"Confidence {confidenceSnapshot.ScorePercentage}%",
                confidenceSnapshot.IsVetoed
                    ? $"Veto {confidenceSnapshot.RiskReasonCode}"
                    : confidenceSnapshot.Band.ToString()
            ]);
    }

    private static StrategySignalLogExplainabilitySnapshot CreateVetoUiLogSnapshot(
        TradingStrategySignalVeto veto,
        StrategySignalConfidenceSnapshot confidenceSnapshot)
    {
        return new StrategySignalLogExplainabilitySnapshot(
            $"{veto.SignalType} signal vetoed",
            $"{veto.Symbol} {veto.Timeframe} {veto.SignalType.ToString().ToLowerInvariant()} signal vetoed because {FormatRiskReason(confidenceSnapshot.RiskReasonCode)}.",
            [
                $"Risk gate: {FormatRiskReason(confidenceSnapshot.RiskReasonCode)}",
                confidenceSnapshot.Summary
            ],
            [
                veto.Symbol,
                veto.Timeframe,
                veto.SignalType.ToString(),
                $"Confidence {confidenceSnapshot.ScorePercentage}%",
                $"Veto {confidenceSnapshot.RiskReasonCode}"
            ]);
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
        TradingStrategyVersion version,
        StrategyEvaluationContext context,
        StrategyEvaluationResult evaluationResult,
        StrategySignalType? signalType,
        string decisionOutcome,
        string? vetoReasonCode,
        int? riskScore,
        Guid? relatedEntityId)
    {
        return JsonSerializer.Serialize(
            new
            {
                TradingStrategyVersionId = version.Id,
                StrategyVersionNumber = version.VersionNumber,
                StrategySchemaVersion = version.SchemaVersion,
                ExecutionEnvironment = context.Mode.ToString(),
                context.IndicatorSnapshot.Symbol,
                context.IndicatorSnapshot.Timeframe,
                context.IndicatorSnapshot.CloseTimeUtc,
                context.IndicatorSnapshot.ReceivedAtUtc,
                SignalType = signalType?.ToString() ?? "None",
                evaluationResult.EntryMatched,
                evaluationResult.ExitMatched,
                evaluationResult.RiskPassed,
                DecisionOutcome = decisionOutcome,
                VetoReasonCode = vetoReasonCode,
                RiskScore = riskScore,
                RelatedEntityId = relatedEntityId,
                CapturedAtUtc = timeProvider.GetUtcNow().UtcDateTime
            },
            SerializerOptions);
    }
}



