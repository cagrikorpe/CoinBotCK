namespace CoinBot.Application.Abstractions.Administration;

public enum AdminCommandStartDisposition
{
    Started = 0,
    AlreadyCompleted = 1,
    AlreadyRunning = 2,
    PayloadConflict = 3
}
