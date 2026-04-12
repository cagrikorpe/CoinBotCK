using System.Globalization;
using System.Security.Claims;
using System.Text;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace CoinBot.Web.Controllers;

[RedirectSuperAdminToAdminOverview]
[Authorize(Policy = ApplicationPolicies.TradeOperations)]
public class StrategyBuilderController(
    IStrategyTemplateCatalogService templateCatalogService,
    IStrategyVersionService strategyVersionService,
    ApplicationDbContext dbContext) : Controller
{
    private const int StrategyNameMaxLength = 128;
    private const int StrategyKeyMaxLength = 128;

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
                    : $"{entity.DisplayName} ({entity.StrategyKey})",
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
            TempData["StrategyBuilderTemplateError"] = "Template ve strateji seçimi gerekli.";
            return RedirectToAction(nameof(Index));
        }

        var targetStrategy = await dbContext.TradingStrategies
            .AsNoTracking()
            .Where(entity => entity.Id == strategyId &&
                             entity.OwnerUserId == userId &&
                             !entity.IsDeleted)
            .Select(entity => new
            {
                entity.StrategyKey,
                DisplayLabel = string.IsNullOrWhiteSpace(entity.DisplayName)
                    ? entity.StrategyKey
                    : $"{entity.DisplayName} ({entity.StrategyKey})"
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (targetStrategy is null)
        {
            TempData["StrategyBuilderTemplateError"] = "Strateji bulunamadı.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var draft = await strategyVersionService.CreateDraftFromTemplateAsync(
                strategyId,
                normalizedTemplateKey,
                cancellationToken);
            _ = await strategyVersionService.PublishAsync(draft.StrategyVersionId, cancellationToken);

            TempData["StrategyBuilderTemplateSuccess"] = "Strateji güncellendi. Bot ekranında seçebilirsiniz.";
        }
        catch (StrategyDefinitionValidationException)
        {
            TempData["StrategyBuilderTemplateError"] = "Strateji taslağı oluşturulamadı.";
        }
        catch (StrategyRuleParseException)
        {
            TempData["StrategyBuilderTemplateError"] = "Strateji taslağı oluşturulamadı.";
        }
        catch (StrategyTemplateCatalogException)
        {
            TempData["StrategyBuilderTemplateError"] = "Template kullanılamıyor.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            TempData["StrategyBuilderTemplateError"] = "Strateji taslağı oluşturulamadı.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("StrategyBuilder/StartFromTemplate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartFromTemplate(
        string templateKey,
        string? strategyName,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Challenge();
        }

        var normalizedTemplateKey = templateKey?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTemplateKey))
        {
            TempData["StrategyBuilderTemplateError"] = "Template seçimi gerekli.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var template = await templateCatalogService.GetAsync(normalizedTemplateKey, cancellationToken);
            var displayName = NormalizeStrategyName(strategyName, template.TemplateName);
            var strategy = new TradingStrategy
            {
                Id = Guid.NewGuid(),
                OwnerUserId = userId,
                StrategyKey = await GenerateStrategyKeyAsync(userId, displayName, cancellationToken),
                DisplayName = displayName
            };

            dbContext.TradingStrategies.Add(strategy);
            await dbContext.SaveChangesAsync(cancellationToken);
            var draft = await strategyVersionService.CreateDraftFromTemplateAsync(strategy.Id, normalizedTemplateKey, cancellationToken);
            _ = await strategyVersionService.PublishAsync(draft.StrategyVersionId, cancellationToken);

            TempData["StrategyBuilderTemplateSuccess"] = "Strateji oluşturuldu. Bot oluştururken bu stratejiyi seçebilirsiniz.";
        }
        catch (StrategyTemplateCatalogException)
        {
            TempData["StrategyBuilderTemplateError"] = "Template kullanılamıyor.";
        }
        catch (StrategyDefinitionValidationException)
        {
            TempData["StrategyBuilderTemplateError"] = "Strateji taslağı oluşturulamadı.";
        }
        catch (StrategyRuleParseException)
        {
            TempData["StrategyBuilderTemplateError"] = "Strateji taslağı oluşturulamadı.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or DbUpdateException)
        {
            TempData["StrategyBuilderTemplateError"] = "Strateji taslağı oluşturulamadı.";
        }

        return RedirectToAction(nameof(Index));
    }

    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    private async Task<string> GenerateStrategyKeyAsync(
        string ownerUserId,
        string displayName,
        CancellationToken cancellationToken)
    {
        var baseKey = Slugify(displayName);
        for (var index = 0; index < 50; index++)
        {
            var candidate = index == 0
                ? baseKey
                : AppendStrategyKeySuffix(baseKey, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var exists = await dbContext.TradingStrategies
                .AsNoTracking()
                .IgnoreQueryFilters()
                .AnyAsync(entity =>
                    entity.OwnerUserId == ownerUserId &&
                    entity.StrategyKey == candidate &&
                    !entity.IsDeleted,
                    cancellationToken);
            if (!exists)
            {
                return candidate;
            }
        }

        return AppendStrategyKeySuffix(baseKey, Guid.NewGuid().ToString("N"));
    }

    private static string NormalizeStrategyName(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? fallback.Trim()
            : value.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "Yeni Strateji";
        }

        return normalized.Length <= StrategyNameMaxLength
            ? normalized
            : normalized[..StrategyNameMaxLength];
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            var normalizedCharacter = character switch
            {
                'ç' => 'c',
                'ğ' => 'g',
                'ı' => 'i',
                'ö' => 'o',
                'ş' => 's',
                'ü' => 'u',
                _ => character
            };

            if (char.IsLetterOrDigit(normalizedCharacter))
            {
                builder.Append(normalizedCharacter);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug)
            ? "strateji"
            : TrimStrategyKey(slug);
    }

    private static string AppendStrategyKeySuffix(string baseKey, string suffix)
    {
        var normalizedSuffix = $"-{suffix}";
        return baseKey.Length + normalizedSuffix.Length <= StrategyKeyMaxLength
            ? baseKey + normalizedSuffix
            : baseKey[..(StrategyKeyMaxLength - normalizedSuffix.Length)].TrimEnd('-') + normalizedSuffix;
    }

    private static string TrimStrategyKey(string value)
    {
        return value.Length <= StrategyKeyMaxLength
            ? value
            : value[..StrategyKeyMaxLength].TrimEnd('-');
    }
}
