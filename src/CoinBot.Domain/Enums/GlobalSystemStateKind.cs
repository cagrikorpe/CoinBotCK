namespace CoinBot.Domain.Enums;

public enum GlobalSystemStateKind
{
    Active = 0,
    SoftHalt = 1,
    FullHalt = 2,
    Maintenance = 3,
    Degraded = 4
}
