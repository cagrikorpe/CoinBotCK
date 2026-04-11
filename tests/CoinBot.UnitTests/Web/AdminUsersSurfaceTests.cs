using System.IO;
using System.Threading;
using CoinBot.Contracts.Common;
using CoinBot.Web.Areas.Admin.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CoinBot.UnitTests.Web;

public sealed class AdminUsersSurfaceTests
{
    [Fact]
    public void AdminUsersView_RendersSoftUserManagementModules()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Admin",
            "Users.cshtml"));

        Assert.Contains("data-cb-admin-users-soft-module", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-users-list", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-users-new", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-users-approval", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-users-role-security", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-users-search", content, StringComparison.Ordinal);
        Assert.Contains("Kullanıcı Listesi", content, StringComparison.Ordinal);
        Assert.Contains("Yeni Kullanıcı Ekle", content, StringComparison.Ordinal);
        Assert.Contains("Kullanıcı Onay", content, StringComparison.Ordinal);
        Assert.Contains("Kullanıcı Detayı", content, StringComparison.Ordinal);
        Assert.Contains("Rol ve Güvenlik", content, StringComparison.Ordinal);
        Assert.Contains("Kullanıcı adı", content, StringComparison.Ordinal);
        Assert.Contains("Rol", content, StringComparison.Ordinal);
        Assert.Contains("Durum", content, StringComparison.Ordinal);
        Assert.Contains("MFA", content, StringComparison.Ordinal);
        Assert.Contains("Son giriş", content, StringComparison.Ordinal);
        Assert.Contains("Kayıt ekranı", content, StringComparison.Ordinal);
        Assert.Contains("Onay bekleyenleri göster", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-users-approve-form", content, StringComparison.Ordinal);
        Assert.Contains("ApproveUser", content, StringComparison.Ordinal);
        Assert.Contains("Onayla", content, StringComparison.Ordinal);

        Assert.DoesNotContain("audit", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("incident", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trace", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timeline", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdminUserDetailView_RendersSoftDetailAndSecurityActions()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Admin",
            "UserDetail.cshtml"));

        Assert.Contains("data-cb-admin-user-detail-soft", content, StringComparison.Ordinal);
        Assert.Contains("Temel Bilgiler", content, StringComparison.Ordinal);
        Assert.Contains("Rol ve Güvenlik", content, StringComparison.Ordinal);
        Assert.Contains("Kullanıcı adı", content, StringComparison.Ordinal);
        Assert.Contains("Görünen ad", content, StringComparison.Ordinal);
        Assert.Contains("Durum", content, StringComparison.Ordinal);
        Assert.Contains("MFA", content, StringComparison.Ordinal);
        Assert.Contains("Bağlı bot sayısı", content, StringComparison.Ordinal);
        Assert.Contains("Exchange bağlı mı", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-user-security-actions", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-user-approve-form", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-user-approve", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-user-action=\"disable\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-user-action=\"password-reset\"", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-user-action=\"mfa-reset\"", content, StringComparison.Ordinal);
        Assert.Contains("Pasife al", content, StringComparison.Ordinal);
        Assert.Contains("Şifre sıfırla", content, StringComparison.Ordinal);
        Assert.Contains("MFA sıfırla", content, StringComparison.Ordinal);

        Assert.DoesNotContain("audit", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("incident", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trace", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timeline", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Risk", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ApproveUserAction_UsesPostCsrfAndIdentityAdministrationPolicy()
    {
        var method = typeof(AdminController).GetMethod(nameof(AdminController.ApproveUser), [typeof(string), typeof(CancellationToken)]);

        Assert.NotNull(method);
        Assert.Contains(method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true), attribute => attribute is HttpPostAttribute);
        Assert.Contains(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true), attribute => attribute is ValidateAntiForgeryTokenAttribute);
        var authorize = Assert.Single(method.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).OfType<AuthorizeAttribute>());
        Assert.Equal(ApplicationPolicies.IdentityAdministration, authorize.Policy);
    }

    private static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "CoinBot.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new DirectoryNotFoundException("CoinBot repository root could not be resolved.");
        }

        return directory.FullName;
    }
}



