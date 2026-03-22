namespace CoinBot.Domain.Enums;

public enum ExchangePrivateStreamConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    ListenKeyExpired = 4
}
