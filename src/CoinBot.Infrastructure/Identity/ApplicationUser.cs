using Microsoft.AspNetCore.Identity;

namespace CoinBot.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;
}
