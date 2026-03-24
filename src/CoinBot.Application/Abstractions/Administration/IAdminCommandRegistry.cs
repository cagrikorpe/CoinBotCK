namespace CoinBot.Application.Abstractions.Administration;

public interface IAdminCommandRegistry
{
    Task<AdminCommandStartResult> TryStartAsync(
        AdminCommandStartRequest request,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        AdminCommandCompletionRequest request,
        CancellationToken cancellationToken = default);
}
