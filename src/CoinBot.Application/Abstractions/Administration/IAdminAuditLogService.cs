namespace CoinBot.Application.Abstractions.Administration;

public interface IAdminAuditLogService
{
    Task WriteAsync(AdminAuditLogWriteRequest request, CancellationToken cancellationToken = default);
}
