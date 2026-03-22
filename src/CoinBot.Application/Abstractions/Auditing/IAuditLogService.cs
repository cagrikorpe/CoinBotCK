namespace CoinBot.Application.Abstractions.Auditing;

public interface IAuditLogService
{
    Task WriteAsync(AuditLogWriteRequest request, CancellationToken cancellationToken = default);
}
