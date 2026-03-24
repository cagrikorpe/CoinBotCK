using System.Security.Claims;

namespace CoinBot.Contracts.Common;

public static class ApplicationRoleClaims
{
    private static readonly IReadOnlyDictionary<string, string[]> PermissionsByRole = new Dictionary<string, string[]>
    {
        [ApplicationRoles.SuperAdmin] =
        [
            .. ApplicationPermissions.All
        ],
        [ApplicationRoles.OpsAdmin] =
        [
            ApplicationPermissions.AdminPortalAccess,
            ApplicationPermissions.IdentityAdministration,
            ApplicationPermissions.TradeOperations,
            ApplicationPermissions.RiskManagement,
            ApplicationPermissions.ExchangeManagement,
            ApplicationPermissions.AuditRead
        ],
        [ApplicationRoles.SecurityAuditor] =
        [
            ApplicationPermissions.AdminPortalAccess,
            ApplicationPermissions.AuditRead
        ],
        [ApplicationRoles.Admin] =
        [
            ApplicationPermissions.AdminPortalAccess,
            ApplicationPermissions.IdentityAdministration,
            ApplicationPermissions.TradeOperations,
            ApplicationPermissions.RiskManagement,
            ApplicationPermissions.ExchangeManagement,
            ApplicationPermissions.AuditRead
        ],
        [ApplicationRoles.Support] =
        [
            ApplicationPermissions.AdminPortalAccess,
            ApplicationPermissions.AuditRead
        ],
        [ApplicationRoles.Auditor] =
        [
            ApplicationPermissions.AdminPortalAccess,
            ApplicationPermissions.AuditRead
        ],
        [ApplicationRoles.User] =
        [
            ApplicationPermissions.TradeOperations,
            ApplicationPermissions.RiskManagement,
            ApplicationPermissions.ExchangeManagement
        ]
    };

    public static IReadOnlyCollection<string> GetPermissions(string roleName)
    {
        return PermissionsByRole.TryGetValue(roleName, out var permissions)
            ? permissions
            : Array.Empty<string>();
    }

    public static IReadOnlyCollection<Claim> GetClaims(string roleName)
    {
        return GetPermissions(roleName)
            .Select(permission => new Claim(ApplicationClaimTypes.Permission, permission))
            .ToArray();
    }
}
