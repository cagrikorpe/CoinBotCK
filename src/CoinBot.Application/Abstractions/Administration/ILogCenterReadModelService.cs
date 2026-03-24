namespace CoinBot.Application.Abstractions.Administration;

public interface ILogCenterReadModelService
{
    Task<LogCenterPageSnapshot> GetPageAsync(
        LogCenterQueryRequest request,
        CancellationToken cancellationToken = default);
}
