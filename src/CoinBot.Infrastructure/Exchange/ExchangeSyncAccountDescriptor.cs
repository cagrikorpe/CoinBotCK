namespace CoinBot.Infrastructure.Exchange;

internal sealed record ExchangeSyncAccountDescriptor(
    Guid ExchangeAccountId,
    string OwnerUserId,
    string ExchangeName);
