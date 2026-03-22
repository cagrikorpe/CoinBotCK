using System.Text.Json;
using System.Text.Json.Serialization;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Strategies;

public sealed class StrategySignalService(
    ApplicationDbContext dbContext,
    IStrategyEvaluatorService evaluator,
    IRiskPolicyEvaluator riskPolicyEvaluator,
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
        var evaluationResult = evaluator.Evaluate(version.DefinitionJson, normalizedContext);
        var candidateSignalTypes = GetCandidateSignalTypes(evaluationResult);

        if (candidateSignalTypes.Count == 0)
        {
            logger.LogInformation(
                "Strategy signal generation produced no persisted signals for StrategyVersionId {StrategyVersionId}.",
                version.Id);

            return new StrategySignalGenerationResult(
                evaluationResult,
                Array.Empty<StrategySignalSnapshot>(),
                Array.Empty<StrategySignalVetoSnapshot>(),
                SuppressedDuplicateCount: 0);
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
                    veto = CreateVetoEntity(strategy, version, signalType, normalizedContext, riskEvaluation);
                    dbContext.TradingStrategySignalVetoes.Add(veto);
                    hasPendingChanges = true;
                }

                vetoSnapshots.Add(ToVetoSnapshot(veto, riskEvaluation));

                logger.LogInformation(
                    "Strategy signal vetoed for StrategyVersionId {StrategyVersionId}, SignalType {SignalType}, Symbol {Symbol}, Timeframe {Timeframe}, ReasonCode {ReasonCode}.",
                    version.Id,
                    signalType,
                    normalizedContext.IndicatorSnapshot.Symbol,
                    normalizedContext.IndicatorSnapshot.Timeframe,
                    riskEvaluation.ReasonCode);

                continue;
            }

            var signal = CreateSignalEntity(strategy, version, signalType, normalizedContext, evaluationResult, riskEvaluation);
            dbContext.TradingStrategySignals.Add(signal);
            persistedSignals.Add(signal);
            hasPendingChanges = true;
        }

        if (hasPendingChanges)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var snapshots = persistedSignals
            .Select(signal => ToSnapshot(
                signal,
                normalizedContext.IndicatorSnapshot,
                evaluationResult,
                DeserializeOptional<RiskVetoResult>(signal.RiskEvaluationJson)))
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
            suppressedDuplicateCount);
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
        var riskEvaluation = DeserializeOptional<RiskVetoResult>(signal.RiskEvaluationJson);

        return ToSnapshot(signal, indicatorSnapshot, evaluationResult, riskEvaluation);
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

        var riskEvaluation = DeserializeRequired<RiskVetoResult>(veto.RiskEvaluationJson);

        return ToVetoSnapshot(veto, riskEvaluation);
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
        RiskVetoResult riskEvaluation)
    {
        return new TradingStrategySignal
        {
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
            GeneratedAtUtc = riskEvaluation.Snapshot.EvaluatedAtUtc,
            ExplainabilitySchemaVersion = ExplainabilitySchemaVersion,
            IndicatorSnapshotJson = JsonSerializer.Serialize(context.IndicatorSnapshot, SerializerOptions),
            RuleResultSnapshotJson = JsonSerializer.Serialize(evaluationResult, SerializerOptions),
            RiskEvaluationJson = JsonSerializer.Serialize(riskEvaluation, SerializerOptions)
        };
    }

    private static TradingStrategySignalVeto CreateVetoEntity(
        TradingStrategy strategy,
        TradingStrategyVersion version,
        StrategySignalType signalType,
        StrategyEvaluationContext context,
        RiskVetoResult riskEvaluation)
    {
        return new TradingStrategySignalVeto
        {
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
            RiskEvaluationJson = JsonSerializer.Serialize(riskEvaluation, SerializerOptions)
        };
    }

    private static StrategySignalSnapshot ToSnapshot(
        TradingStrategySignal signal,
        StrategyIndicatorSnapshot indicatorSnapshot,
        StrategyEvaluationResult evaluationResult,
        RiskVetoResult? riskEvaluation)
    {
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
                riskEvaluation));
    }

    private static StrategySignalVetoSnapshot ToVetoSnapshot(
        TradingStrategySignalVeto veto,
        RiskVetoResult riskEvaluation)
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
            riskEvaluation);
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

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }
}
