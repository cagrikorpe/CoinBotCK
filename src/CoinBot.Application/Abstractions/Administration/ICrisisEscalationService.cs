namespace CoinBot.Application.Abstractions.Administration;

public interface ICrisisEscalationService
{
    Task<CrisisEscalationPreview> PreviewAsync(
        CrisisEscalationPreviewRequest request,
        CancellationToken cancellationToken = default);

    Task<CrisisEscalationExecutionResult> ExecuteAsync(
        CrisisEscalationExecuteRequest request,
        CancellationToken cancellationToken = default);
}
