using CoinBot.Domain.Enums;

namespace CoinBot.Application.Abstractions.Ai;

public interface ITrainingDatasetBuilderService
{
    Task<TrainingDatasetBuildSnapshot> BuildAsync(
        TrainingDatasetBuildRequest request,
        CancellationToken cancellationToken = default);

    Task<TrainingDatasetExportSnapshot> ExportCsvAsync(
        TrainingDatasetBuildRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TrainingDatasetBuildRequest(
    string UserId,
    Guid? BotId = null,
    string? Symbol = null,
    string? Timeframe = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    AiShadowOutcomeHorizonKind HorizonKind = AiShadowOutcomeDefaults.OfficialHorizonKind,
    int HorizonValue = AiShadowOutcomeDefaults.OfficialHorizonValue,
    int Take = 500,
    bool TrainingEligibleOnly = true);

public sealed record TrainingDatasetBuildSnapshot(
    string UserId,
    Guid? BotId,
    string? Symbol,
    string? Timeframe,
    AiShadowOutcomeHorizonKind HorizonKind,
    int HorizonValue,
    int SourceRowCount,
    int RowCount,
    int TrainingEligibleRowCount,
    IReadOnlyCollection<TrainingDatasetLabelDefinitionSnapshot> LabelDefinitions,
    IReadOnlyCollection<TrainingDatasetLeakageRuleSnapshot> LeakageRules,
    IReadOnlyCollection<TrainingDatasetColumnSnapshot> Columns,
    IReadOnlyCollection<TrainingDatasetRowSnapshot> Rows);

public sealed record TrainingDatasetExportSnapshot(
    string FileName,
    string ContentType,
    string CsvContent,
    int SourceRowCount,
    int RowCount,
    IReadOnlyCollection<string> ColumnOrder);

public sealed record TrainingDatasetColumnSnapshot(
    string Name,
    TrainingDatasetColumnGroup Group,
    string DataType,
    string Description);

public sealed record TrainingDatasetLabelDefinitionSnapshot(
    string Name,
    string Description,
    bool Nullable,
    string Notes);

public sealed record TrainingDatasetLeakageRuleSnapshot(string ColumnName, string Reason);

public sealed record TrainingDatasetRowSnapshot(
    Guid FeatureSnapshotId,
    Guid AiShadowDecisionId,
    string SplitBucket,
    bool IsTrainingEligible,
    IReadOnlyDictionary<string, string?> Values);

public enum TrainingDatasetColumnGroup
{
    Metadata = 0,
    Feature = 1,
    Label = 2
}
