using System.IO;

namespace CoinBot.UnitTests.Web;

public sealed class AdminSettingsSurfaceTests
{
    [Fact]
    public void AdminSettingsView_UsesTabbedOperationalLayout()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Admin",
            "Settings.cshtml"));

        Assert.Contains("cb_admin_settings_tab_overview", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_settings_tab_wizard", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_settings_tab_critical", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_settings_tab_sync", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_settings_tab_activation", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_settings_tab_details", content, StringComparison.Ordinal);
        Assert.Contains("Canli Snapshot / Salt Okunur", content, StringComparison.Ordinal);
        Assert.Contains("Yazilabilir Konfigürasyon", content, StringComparison.Ordinal);
        Assert.Contains("_AdminActivationControlCenterSection", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_settings_policy_restrictions", content, StringComparison.Ordinal);
        Assert.Contains("function activateSettingsHash()", content, StringComparison.Ordinal);
        var riskPolicyContent = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(), "src", "CoinBot.Web", "Areas", "Admin", "Views", "Shared", "Foundation", "_AdminRiskPolicyDefaultsSection.cshtml"));
        Assert.Contains("Open icin satiri kaldirin.", riskPolicyContent, StringComparison.Ordinal);
        Assert.Contains("degisiklik dogrudan uygulanir", riskPolicyContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Approval Merkezi", riskPolicyContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Onay bekleyen policy degisikligi", riskPolicyContent, StringComparison.Ordinal);
        Assert.DoesNotContain("kaldirma istegi onaylanmadan", riskPolicyContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Onay detayini ac", riskPolicyContent, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-action=\"ApprovalDetail\"", riskPolicyContent, StringComparison.Ordinal);

        Assert.True(
            content.IndexOf("cb_admin_settings_tab_overview", StringComparison.Ordinal) < content.IndexOf("cb_admin_settings_tab_critical", StringComparison.Ordinal),
            "Overview tab should appear before the critical settings tab.");
        Assert.True(
            content.IndexOf("cb_admin_settings_tab_critical", StringComparison.Ordinal) < content.IndexOf("cb_admin_settings_tab_details", StringComparison.Ordinal),
            "Critical settings tab should appear before the details tab.");
    }

    [Fact]
    public void AdminActivationControlCenterPartial_RendersCriticalBlocks()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "Foundation",
            "_AdminActivationControlCenterSection.cshtml"));

        Assert.Contains("Sistem Aktivasyon Kontrol Merkezi", content, StringComparison.Ordinal);
        Assert.Contains("Readiness checklist", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-activation-control-center", content, StringComparison.Ordinal);
        Assert.Contains("data-cb-admin-activation-check-item", content, StringComparison.Ordinal);
        Assert.Contains("ActivateSystem", content, StringComparison.Ordinal);
        Assert.Contains("DeactivateSystem", content, StringComparison.Ordinal);
        Assert.Contains("Onay ibaresi", content, StringComparison.Ordinal);
        Assert.Contains("ONAYLA", content, StringComparison.Ordinal);
        Assert.Contains("cb_admin_settings_crisis_panel", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminCriticalSwitchPartials_RequireConfirmationPhrase()
    {
        var executionContent = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "Foundation",
            "_AdminGlobalExecutionSwitchSection.cshtml"));
        var globalStateContent = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "Foundation",
            "_AdminGlobalSystemStateSection.cshtml"));

        Assert.Contains("Onay ibaresi", executionContent, StringComparison.Ordinal);
        Assert.Contains("ONAYLA", executionContent, StringComparison.Ordinal);
        Assert.Contains("Onay ibaresi", globalStateContent, StringComparison.Ordinal);
        Assert.Contains("ONAYLA", globalStateContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSettingsSectionNav_ContainsExpectedTabOrder()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "Foundation",
            "_AdminGlobalSettingsSectionNav.cshtml"));

        var overviewIndex = content.IndexOf("cb_admin_settings_tab_link_overview", StringComparison.Ordinal);
        var wizardIndex = content.IndexOf("cb_admin_settings_tab_link_wizard", StringComparison.Ordinal);
        var criticalIndex = content.IndexOf("cb_admin_settings_tab_link_critical", StringComparison.Ordinal);
        var syncIndex = content.IndexOf("cb_admin_settings_tab_link_sync", StringComparison.Ordinal);
        var activationIndex = content.IndexOf("cb_admin_settings_tab_link_activation", StringComparison.Ordinal);
        var detailsIndex = content.IndexOf("cb_admin_settings_tab_link_details", StringComparison.Ordinal);

        Assert.True(overviewIndex >= 0 && wizardIndex > overviewIndex && criticalIndex > wizardIndex && syncIndex > criticalIndex && activationIndex > syncIndex && detailsIndex > activationIndex,
            "Admin settings section nav should expose the expected tab sequence.");
    }

    [Fact]
    public void AdminSidebar_ContainsGlobalSettingsLabel()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Areas",
            "Admin",
            "Views",
            "Shared",
            "_AdminSidebar.cshtml"));

        Assert.Contains("Label = \"Global Ayarlar\"", content, StringComparison.Ordinal);
        Assert.Contains("Action = \"Settings\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void UserSettingsView_RemainsSeparateFromAdminGlobalSettingsSurface()
    {
        var content = File.ReadAllText(Path.Combine(
            ResolveRepositoryRoot(),
            "src",
            "CoinBot.Web",
            "Views",
            "Settings",
            "Index.cshtml"));

        Assert.DoesNotContain("cb_admin_settings_tab_content", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Sistem Aktivasyon Kontrol Merkezi", content, StringComparison.Ordinal);
        Assert.Contains("Günlük kullanım tercihlerinizi", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Super Admin/Ops Global Ayarlar", content, StringComparison.Ordinal);
        Assert.DoesNotContain("drift guard", content, StringComparison.OrdinalIgnoreCase);
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
