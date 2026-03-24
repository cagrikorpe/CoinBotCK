namespace CoinBot.Application.Abstractions.Administration;

public interface IAdminWorkspaceReadModelService
{
    Task<AdminUsersPageSnapshot> GetUsersAsync(
        string? query = null,
        string? status = null,
        string? mfa = null,
        CancellationToken cancellationToken = default);

    Task<AdminUserDetailPageSnapshot?> GetUserDetailAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<AdminBotOperationsPageSnapshot> GetBotOperationsAsync(
        string? query = null,
        string? status = null,
        string? mode = null,
        CancellationToken cancellationToken = default);

    Task<AdminStrategyAiMonitoringPageSnapshot> GetStrategyAiMonitoringAsync(
        string? query = null,
        CancellationToken cancellationToken = default);

    Task<AdminSupportLookupSnapshot> GetSupportLookupAsync(
        string? query = null,
        CancellationToken cancellationToken = default);

    Task<AdminSecurityEventsPageSnapshot> GetSecurityEventsAsync(
        string? query = null,
        string? severity = null,
        string? module = null,
        CancellationToken cancellationToken = default);

    Task<AdminNotificationsPageSnapshot> GetNotificationsAsync(
        string? severity = null,
        string? category = null,
        CancellationToken cancellationToken = default);
}
