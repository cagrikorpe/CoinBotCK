namespace CoinBot.Contracts.Common;

public static class ApplicationRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string User = "User";

    public static readonly string[] All =
    [
        SuperAdmin,
        Admin,
        User
    ];
}
