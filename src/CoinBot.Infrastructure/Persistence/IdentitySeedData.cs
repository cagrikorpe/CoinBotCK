using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoinBot.Infrastructure.Persistence;

public static class IdentitySeedData
{
    public static async Task EnsureIdentitySeedDataAsync(this IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();

        var scopedServices = scope.ServiceProvider;
        var logger = scopedServices.GetRequiredService<ILoggerFactory>().CreateLogger("CoinBot.IdentitySeed");
        var dbContext = scopedServices.GetRequiredService<ApplicationDbContext>();
        var roleManager = scopedServices.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scopedServices.GetRequiredService<UserManager<ApplicationUser>>();

        await dbContext.Database.MigrateAsync(cancellationToken);

        foreach (var roleName in ApplicationRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var createRoleResult = await roleManager.CreateAsync(new IdentityRole(roleName));

                if (!createRoleResult.Succeeded)
                {
                    throw new InvalidOperationException($"Failed to create role '{roleName}'.");
                }
            }
        }

        var superAdminEmail = configuration["IdentitySeed:SuperAdminEmail"];
        var superAdminPassword = configuration["IdentitySeed:SuperAdminPassword"];
        var superAdminFullName = configuration["IdentitySeed:SuperAdminFullName"];

        if (string.IsNullOrWhiteSpace(superAdminEmail) || string.IsNullOrWhiteSpace(superAdminPassword))
        {
            logger.LogInformation("Identity roles seeded. Super admin creation skipped because secure seed configuration was not provided.");
            return;
        }

        var user = await userManager.FindByEmailAsync(superAdminEmail);

        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = superAdminEmail,
                Email = superAdminEmail,
                FullName = string.IsNullOrWhiteSpace(superAdminFullName) ? "Super Admin" : superAdminFullName,
                EmailConfirmed = true,
                LockoutEnabled = true,
                TwoFactorEnabled = false
            };

            var createUserResult = await userManager.CreateAsync(user, superAdminPassword);

            if (!createUserResult.Succeeded)
            {
                throw new InvalidOperationException("Failed to create the initial super admin user.");
            }
        }

        if (!await userManager.IsInRoleAsync(user, ApplicationRoles.SuperAdmin))
        {
            var addRoleResult = await userManager.AddToRoleAsync(user, ApplicationRoles.SuperAdmin);

            if (!addRoleResult.Succeeded)
            {
                throw new InvalidOperationException("Failed to assign the SuperAdmin role to the initial user.");
            }
        }

        logger.LogInformation("Identity roles and initial super admin seed completed.");
    }
}
