namespace CoinBot.Contracts.Common;

public static class ApplicationPermissions
{
    public const string AdminPortalAccess = "admin.portal.access";
    public const string IdentityAdministration = "identity.manage";
    public const string TradeOperations = "trade.operations";
    public const string RiskManagement = "risk.manage";
    public const string ExchangeManagement = "exchange.manage";
    public const string AuditRead = "audit.read";
    public const string PlatformAdministration = "platform.manage";

    public static readonly string[] All =
    [
        AdminPortalAccess,
        IdentityAdministration,
        TradeOperations,
        RiskManagement,
        ExchangeManagement,
        AuditRead,
        PlatformAdministration
    ];
}
