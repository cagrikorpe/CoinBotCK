using System.Security.Claims;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Identity;
using CoinBot.Web.Controllers;
using CoinBot.Web.ViewModels.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using IdentitySignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace CoinBot.UnitTests.Web;

public sealed class AuthControllerTests
{
    [Fact]
    public async Task Register_CreatesUserAssignsRoleAndPermissionClaims_AndRedirectsToLogin()
    {
        var userManager = new TestUserManager();
        var signInManager = new TestSignInManager(userManager);
        var controller = CreateController(userManager, signInManager);
        const string returnUrl = "/PaperTrading";

        var result = await controller.Register(new RegisterViewModel
        {
            FullName = "Demo User",
            Email = "demo.user@coinbot.test",
            Password = "Passw0rd!",
            ConfirmPassword = "Passw0rd!",
            AcceptRiskDisclosure = true,
            ReturnUrl = returnUrl
        });

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        var user = Assert.Single(userManager.UsersById.Values);
        var userClaims = await userManager.GetClaimsAsync(user);

        Assert.Equal("Login", redirectResult.ActionName);
        Assert.Equal(returnUrl, redirectResult.RouteValues!["returnUrl"]);
        Assert.Equal(ApplicationRoles.User, Assert.Single(await userManager.GetRolesAsync(user)));
        Assert.Contains(userClaims, claim => claim.Type == ApplicationClaimTypes.Permission && claim.Value == ApplicationPermissions.TradeOperations);
        Assert.Contains(userClaims, claim => claim.Type == ApplicationClaimTypes.Permission && claim.Value == ApplicationPermissions.RiskManagement);
        Assert.Contains(userClaims, claim => claim.Type == ApplicationClaimTypes.Permission && claim.Value == ApplicationPermissions.ExchangeManagement);
        Assert.Equal("Kayıt tamamlandı. Giriş yapabilirsiniz.", controller.TempData["AuthSuccess"]);
    }

    [Fact]
    public async Task Login_SynchronizesPermissionClaimsRefreshesCookie_AndRedirectsToLocalReturnUrl()
    {
        var userManager = new TestUserManager();
        var signInManager = new TestSignInManager(userManager)
        {
            PasswordSignInResult = IdentitySignInResult.Success
        };
        var user = userManager.SeedUser("trade.user@coinbot.test", roles: [ApplicationRoles.User]);
        var controller = CreateController(userManager, signInManager);

        var result = await controller.Login(new LoginViewModel
        {
            EmailOrUserName = user.Email!,
            Password = "Passw0rd!",
            ReturnUrl = "/Bots"
        });

        var localRedirectResult = Assert.IsType<LocalRedirectResult>(result);
        var userClaims = await userManager.GetClaimsAsync(user);

        Assert.Equal("/Bots", localRedirectResult.Url);
        Assert.True(signInManager.RefreshSignInCalled);
        Assert.Contains(userClaims, claim => claim.Type == ApplicationClaimTypes.Permission && claim.Value == ApplicationPermissions.TradeOperations);
        Assert.Contains(userClaims, claim => claim.Type == ApplicationClaimTypes.Permission && claim.Value == ApplicationPermissions.RiskManagement);
        Assert.Contains(userClaims, claim => claim.Type == ApplicationClaimTypes.Permission && claim.Value == ApplicationPermissions.ExchangeManagement);
    }
    [Fact]
    public async Task Login_WhenSuperAdminTargetsUserFlow_RedirectsToAdminOverview()
    {
        var userManager = new TestUserManager();
        var signInManager = new TestSignInManager(userManager)
        {
            PasswordSignInResult = IdentitySignInResult.Success
        };
        var user = userManager.SeedUser("super.admin@coinbot.test", roles: [ApplicationRoles.SuperAdmin]);
        var controller = CreateController(userManager, signInManager);

        var result = await controller.Login(new LoginViewModel
        {
            EmailOrUserName = user.Email!,
            Password = "Passw0rd!",
            ReturnUrl = "/Settings"
        });

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Overview", redirectResult.ActionName);
        Assert.Equal("Admin", redirectResult.ControllerName);
        Assert.Equal("Admin", redirectResult.RouteValues!["area"]);
    }

    [Fact]
    public async Task Login_WhenSuperAdminTargetsAdminRoute_PreservesLocalRedirect()
    {
        var userManager = new TestUserManager();
        var signInManager = new TestSignInManager(userManager)
        {
            PasswordSignInResult = IdentitySignInResult.Success
        };
        var user = userManager.SeedUser("super.admin@coinbot.test", roles: [ApplicationRoles.SuperAdmin]);
        var controller = CreateController(userManager, signInManager);

        var result = await controller.Login(new LoginViewModel
        {
            EmailOrUserName = user.Email!,
            Password = "Passw0rd!",
            ReturnUrl = "/Admin/Admin/Settings"
        });

        var redirectResult = Assert.IsType<LocalRedirectResult>(result);
        Assert.Equal("/Admin/Admin/Settings", redirectResult.Url);
    }

    [Fact]
    public async Task Logout_SignsOutAndRedirectsToLogin()
    {
        var userManager = new TestUserManager();
        var signInManager = new TestSignInManager(userManager);
        var controller = CreateController(userManager, signInManager, isAuthenticated: true);

        var result = await controller.Logout();

        var redirectResult = Assert.IsType<RedirectToActionResult>(result);

        Assert.True(signInManager.SignOutCalled);
        Assert.Equal("Login", redirectResult.ActionName);
        Assert.Equal("Oturum güvenli şekilde kapatıldı.", controller.TempData["AuthSuccess"]);
    }

    private static AuthController CreateController(
        TestUserManager userManager,
        TestSignInManager signInManager,
        bool isAuthenticated = false)
    {
        var httpContext = new DefaultHttpContext();

        if (isAuthenticated)
        {
            httpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "demo.user@coinbot.test")],
                    IdentityConstants.ApplicationScheme));
        }

        return new AuthController(userManager, signInManager)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider()),
            Url = new TestUrlHelper()
        };
    }

    private sealed class TestUserManager : UserManager<ApplicationUser>
    {
        private readonly Dictionary<string, ApplicationUser> usersById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> rolesByUserId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Claim>> claimsByUserId = new(StringComparer.Ordinal);

        public TestUserManager()
            : base(
                new TestUserStore(),
                Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                new PasswordHasher<ApplicationUser>(),
                [],
                [],
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
                NullLogger<UserManager<ApplicationUser>>.Instance)
        {
        }

        public IReadOnlyDictionary<string, ApplicationUser> UsersById => usersById;

        public ApplicationUser SeedUser(string email, IReadOnlyCollection<string>? roles = null, IReadOnlyCollection<Claim>? claims = null)
        {
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid().ToString("N"),
                UserName = email,
                Email = email,
                FullName = "Seed User"
            };

            usersById[user.Id] = user;
            rolesByUserId[user.Id] = roles is null ? [] : new HashSet<string>(roles, StringComparer.Ordinal);
            claimsByUserId[user.Id] = claims is null ? [] : [.. claims];

            return user;
        }

        public override Task<ApplicationUser?> FindByEmailAsync(string email)
        {
            var user = usersById.Values.SingleOrDefault(entity =>
                string.Equals(entity.Email, email, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(user);
        }

        public override Task<ApplicationUser?> FindByNameAsync(string userName)
        {
            var user = usersById.Values.SingleOrDefault(entity =>
                string.Equals(entity.UserName, userName, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(user);
        }

        public override Task<IdentityResult> CreateAsync(ApplicationUser user, string password)
        {
            user.Id = string.IsNullOrWhiteSpace(user.Id) ? Guid.NewGuid().ToString("N") : user.Id;
            usersById[user.Id] = user;
            rolesByUserId.TryAdd(user.Id, []);
            claimsByUserId.TryAdd(user.Id, []);

            return Task.FromResult(IdentityResult.Success);
        }

        public override Task<IdentityResult> DeleteAsync(ApplicationUser user)
        {
            usersById.Remove(user.Id);
            rolesByUserId.Remove(user.Id);
            claimsByUserId.Remove(user.Id);

            return Task.FromResult(IdentityResult.Success);
        }

        public override Task<IdentityResult> AddToRoleAsync(ApplicationUser user, string role)
        {
            if (!rolesByUserId.TryGetValue(user.Id, out var roles))
            {
                roles = [];
                rolesByUserId[user.Id] = roles;
            }

            roles.Add(role);
            return Task.FromResult(IdentityResult.Success);
        }

        public override Task<IList<string>> GetRolesAsync(ApplicationUser user)
        {
            IList<string> roles = rolesByUserId.TryGetValue(user.Id, out var values)
                ? values.ToArray()
                : [];

            return Task.FromResult(roles);
        }

        public override Task<IList<Claim>> GetClaimsAsync(ApplicationUser user)
        {
            IList<Claim> claims = claimsByUserId.TryGetValue(user.Id, out var values)
                ? values.ToArray()
                : [];

            return Task.FromResult(claims);
        }

        public override Task<IdentityResult> AddClaimsAsync(ApplicationUser user, IEnumerable<Claim> claims)
        {
            if (!claimsByUserId.TryGetValue(user.Id, out var values))
            {
                values = [];
                claimsByUserId[user.Id] = values;
            }

            foreach (var claim in claims)
            {
                if (values.Any(existing => existing.Type == claim.Type && existing.Value == claim.Value))
                {
                    continue;
                }

                values.Add(new Claim(claim.Type, claim.Value));
            }

            return Task.FromResult(IdentityResult.Success);
        }

        public override Task<IdentityResult> RemoveClaimsAsync(ApplicationUser user, IEnumerable<Claim> claims)
        {
            if (!claimsByUserId.TryGetValue(user.Id, out var values))
            {
                return Task.FromResult(IdentityResult.Success);
            }

            foreach (var claim in claims)
            {
                values.RemoveAll(existing => existing.Type == claim.Type && existing.Value == claim.Value);
            }

            return Task.FromResult(IdentityResult.Success);
        }
    }

    private sealed class TestSignInManager : SignInManager<ApplicationUser>
    {
        public TestSignInManager(UserManager<ApplicationUser> userManager)
            : base(
                userManager,
                new HttpContextAccessor(),
                new TestUserClaimsPrincipalFactory(),
                Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
                NullLogger<SignInManager<ApplicationUser>>.Instance,
                new AuthenticationSchemeProvider(Microsoft.Extensions.Options.Options.Create(new AuthenticationOptions())),
                new TestUserConfirmation())
        {
        }

        public IdentitySignInResult PasswordSignInResult { get; set; } = IdentitySignInResult.Success;

        public bool RefreshSignInCalled { get; private set; }

        public bool SignOutCalled { get; private set; }

        public override Task<IdentitySignInResult> PasswordSignInAsync(string userName, string password, bool isPersistent, bool lockoutOnFailure)
        {
            return Task.FromResult(PasswordSignInResult);
        }

        public override Task RefreshSignInAsync(ApplicationUser user)
        {
            RefreshSignInCalled = true;
            return Task.CompletedTask;
        }

        public override Task SignOutAsync()
        {
            SignOutCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class TestUserStore : IUserStore<ApplicationUser>
    {
        public void Dispose()
        {
        }

        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id);

        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.UserName);

        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken)
        {
            user.UserName = userName;
            return Task.CompletedTask;
        }

        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.UserName?.ToUpperInvariant());

        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);

        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);

        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => Task.FromResult<ApplicationUser?>(null);

        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => Task.FromResult<ApplicationUser?>(null);
    }

    private sealed class TestUserClaimsPrincipalFactory : IUserClaimsPrincipalFactory<ApplicationUser>
    {
        public Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.UserName ?? user.Email ?? user.Id)],
                IdentityConstants.ApplicationScheme);

            return Task.FromResult(new ClaimsPrincipal(identity));
        }
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>(StringComparer.Ordinal);

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private sealed class TestUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext => new();

        public string? Action(UrlActionContext actionContext) => null;

        public string? Content(string? contentPath) => contentPath;

        public bool IsLocalUrl(string? url) => !string.IsNullOrWhiteSpace(url) && url.StartsWith("/", StringComparison.Ordinal) && !url.StartsWith("//", StringComparison.Ordinal);

        public string? Link(string? routeName, object? values) => null;

        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }

    private sealed class TestUserConfirmation : IUserConfirmation<ApplicationUser>
    {
        public Task<bool> IsConfirmedAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
        {
            return Task.FromResult(true);
        }
    }
}
