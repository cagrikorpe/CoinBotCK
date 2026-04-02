namespace CoinBot.Application.Abstractions.Bots;

public interface IBotManagementService
{
    Task<BotManagementPageSnapshot> GetPageAsync(string ownerUserId, CancellationToken cancellationToken = default);

    Task<BotManagementEditorSnapshot> GetCreateEditorAsync(string ownerUserId, CancellationToken cancellationToken = default);

    Task<BotManagementEditorSnapshot?> GetEditEditorAsync(string ownerUserId, Guid botId, CancellationToken cancellationToken = default);

    Task<BotManagementSaveResult> CreateAsync(
        string ownerUserId,
        BotManagementSaveCommand command,
        string actor,
        string? correlationId = null,
        CancellationToken cancellationToken = default);

    Task<BotManagementSaveResult> UpdateAsync(
        string ownerUserId,
        Guid botId,
        BotManagementSaveCommand command,
        string actor,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}
