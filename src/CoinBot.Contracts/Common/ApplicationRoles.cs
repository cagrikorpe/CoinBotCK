namespace CoinBot.Contracts.Common;

public static class ApplicationRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string OpsAdmin = "OpsAdmin";
    public const string SecurityAuditor = "SecurityAuditor";
    public const string Admin = "Admin";
    public const string Support = "Support";
    public const string Auditor = "Auditor";
    public const string User = "User";

    public static readonly string[] All =
    [
        SuperAdmin,
        OpsAdmin,
        SecurityAuditor,
        Admin,
        Support,
        Auditor,
        User
    ];
}
