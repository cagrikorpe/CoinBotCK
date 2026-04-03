using System.Globalization;
using System.Security.Claims;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Web.Controllers;

[Authorize(Policy = ApplicationPolicies.TradeOperations)]
public class StrategyBuilderController(
    IStrategyTemplateCatalogService templateCatalogService,
    IStrategyVersionService strategyVersionService,
    ApplicationDbContext dbContext) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Challenge();
        }

        ViewData["StrategyTemplateCatalog"] = await templateCatalogService.ListAsync(cancellationToken);
        ViewData["StrategyBuilderDraftTargets"] = await dbContext.TradingStrategies
            .AsNoTracking()
            .Where(entity => entity.OwnerUserId == userId && !entity.IsDeleted)
            .OrderBy(entity => entity.DisplayName)
            .ThenBy(entity => entity.StrategyKey)
            .Select(entity => new SelectListItem(
                string.IsNullOrWhiteSpace(entity.DisplayName)
                    ? entity.StrategyKey
                    : entity.DisplayName,
                entity.Id.ToString()))
            .ToListAsync(cancellationToken);
        return View();
    }

    [HttpPost("StrategyBuilder/CreateDraftFromTemplate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateDraftFromTemplate(
        Guid strategyId,
        string templateKey,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Challenge();
        }

        var normalizedTemplateKey = templateKey?.Trim();
        if (strategyId == Guid.Empty || string.IsNullOrWhiteSpace(normalizedTemplateKey))
        {
            TempData["StrategyBuilderTemplateError"] = "Template ve hedef strateji seçimi gerekli.";
            return RedirectToAction(nameof(Index));
        }

        var strategyExists = await dbContext.TradingStrategies
            .AsNoTracking()
            .AnyAsync(
                entity => entity.Id == strategyId &&
                          entity.OwnerUserId == userId &&
                          !entity.IsDeleted,
                cancellationToken);
        if (!strategyExists)
        {
            TempData["StrategyBuilderTemplateError"] = "Hedef strateji bulunamadı veya kullanıcı kapsamı dışında.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var draft = await strategyVersionService.CreateDraftFromTemplateAsync(
                strategyId,
                normalizedTemplateKey,
                cancellationToken);

            TempData["StrategyBuilderTemplateSuccess"] = SanitizeTemplateMessage(
                FormattableString.Invariant(
                    $"Draft oluşturuldu. Template={draft.TemplateKey ?? normalizedTemplateKey}; Version={draft.VersionNumber}; Validation={draft.ValidationStatusCode}."));
        }
        catch (StrategyDefinitionValidationException exception)
        {
            TempData["StrategyBuilderTemplateError"] = SanitizeTemplateMessage(
                FormattableString.Invariant(
                    $"{exception.StatusCode}: {exception.Message}; {string.Join(" | ", exception.FailureReasons.Take(2))}"));
        }
        catch (StrategyRuleParseException exception)
        {
            TempData["StrategyBuilderTemplateError"] = SanitizeTemplateMessage(
                $"ParseFailed: {exception.Message}");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            TempData["StrategyBuilderTemplateError"] = SanitizeTemplateMessage(exception.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    private static string SanitizeTemplateMessage(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Template draft sonucu okunamadı.";
        }

        return normalized.Length <= 256
            ? normalized
            : normalized[..256];
    }
}
