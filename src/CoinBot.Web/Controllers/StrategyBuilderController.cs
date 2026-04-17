using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Contracts.Common;
using CoinBot.Domain.Entities;
using CoinBot.Infrastructure.Jobs;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Web.StrategyBuilderSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.Web.Controllers;

[RedirectSuperAdminToAdminOverview]
[Authorize(Policy = ApplicationPolicies.TradeOperations)]
public class StrategyBuilderController(
    IStrategyTemplateCatalogService templateCatalogService,
    IStrategyVersionService strategyVersionService,
    ApplicationDbContext dbContext,
    IOptions<BotExecutionPilotOptions>? botExecutionPilotOptions = null) : Controller
{
    private const int StrategyNameMaxLength = 128;
    private const int StrategyKeyMaxLength = 128;
    private readonly BotExecutionPilotOptions pilotOptionsValue = botExecutionPilotOptions?.Value ?? new BotExecutionPilotOptions();

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Challenge();
        }

        ViewData["StrategyTemplateCatalog"] = await templateCatalogService.ListAsync(cancellationToken);
        ViewData["StrategyBuilderRuntimeConfigJson"] = JsonSerializer.Serialize(
            StrategyBuilderRuntimeParityHelper.BuildRuntimeConfig(pilotOptionsValue));
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
        var normalizedDefinitionJson = ReadPostedDefinitionJson();
        if (strategyId == Guid.Empty ||
            (string.IsNullOrWhiteSpace(normalizedTemplateKey) && string.IsNullOrWhiteSpace(normalizedDefinitionJson)))
        {
            TempData["StrategyBuilderTemplateError"] = "Template veya builder kaydi gerekli.";
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
            _ = await CreateAndPublishDraftAsync(
                strategyId,
                normalizedTemplateKey,
                normalizedDefinitionJson,
                cancellationToken);

            TempData["StrategyBuilderTemplateSuccess"] = "Strateji güncellendi. Bot ekranında seçebilirsiniz.";
        }
        catch (StrategyDefinitionValidationException exception)
        {
            TempData["StrategyBuilderTemplateError"] = string.IsNullOrWhiteSpace(normalizedDefinitionJson)
                ? "Strateji taslağı oluşturulamadı."
                : BuildSafeSaveValidationMessage(exception.StatusCode);
        }
        catch (StrategyRuleParseException exception)
        {
            TempData["StrategyBuilderTemplateError"] = string.IsNullOrWhiteSpace(normalizedDefinitionJson)
                ? "Strateji taslağı oluşturulamadı."
                : BuildSafeSaveParseMessage(exception.Message);
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
        var normalizedDefinitionJson = ReadPostedDefinitionJson();
        if (string.IsNullOrWhiteSpace(normalizedTemplateKey) && string.IsNullOrWhiteSpace(normalizedDefinitionJson))
        {
            TempData["StrategyBuilderTemplateError"] = "Template veya builder kaydi gerekli.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var template = string.IsNullOrWhiteSpace(normalizedTemplateKey)
                ? null
                : await templateCatalogService.GetAsync(normalizedTemplateKey, cancellationToken);
            var displayName = NormalizeStrategyName(strategyName, template?.TemplateName ?? "Yeni Strateji");
            var strategy = new TradingStrategy
            {
                Id = Guid.NewGuid(),
                OwnerUserId = userId,
                StrategyKey = await GenerateStrategyKeyAsync(userId, displayName, cancellationToken),
                DisplayName = displayName
            };

            dbContext.TradingStrategies.Add(strategy);
            await dbContext.SaveChangesAsync(cancellationToken);
            _ = await CreateAndPublishDraftAsync(
                strategy.Id,
                normalizedTemplateKey,
                normalizedDefinitionJson,
                cancellationToken);

            TempData["StrategyBuilderTemplateSuccess"] = "Strateji oluşturuldu. Bot oluştururken bu stratejiyi seçebilirsiniz.";
        }
        catch (StrategyTemplateCatalogException)
        {
            TempData["StrategyBuilderTemplateError"] = "Template kullanılamıyor.";
        }
        catch (StrategyDefinitionValidationException exception)
        {
            TempData["StrategyBuilderTemplateError"] = string.IsNullOrWhiteSpace(normalizedDefinitionJson)
                ? "Strateji taslağı oluşturulamadı."
                : BuildSafeSaveValidationMessage(exception.StatusCode);
        }
        catch (StrategyRuleParseException exception)
        {
            TempData["StrategyBuilderTemplateError"] = string.IsNullOrWhiteSpace(normalizedDefinitionJson)
                ? "Strateji taslağı oluşturulamadı."
                : BuildSafeSaveParseMessage(exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or DbUpdateException)
        {
            TempData["StrategyBuilderTemplateError"] = "Strateji taslağı oluşturulamadı.";
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<StrategyVersionSnapshot> CreateAndPublishDraftAsync(
        Guid strategyId,
        string? templateKey,
        string? definitionJson,
        CancellationToken cancellationToken)
    {
        StrategyVersionSnapshot draft;
        if (!string.IsNullOrWhiteSpace(definitionJson))
        {
            draft = await strategyVersionService.CreateDraftAsync(strategyId, definitionJson, cancellationToken);
        }
        else
        {
            draft = await strategyVersionService.CreateDraftFromTemplateAsync(
                strategyId,
                templateKey ?? throw new ArgumentException("Template key is required.", nameof(templateKey)),
                cancellationToken);
        }

        return await strategyVersionService.PublishAsync(draft.StrategyVersionId, cancellationToken);
    }

    private string? ReadPostedDefinitionJson()
    {
        if (Request?.HasFormContentType != true)
        {
            return null;
        }

        if (!Request.Form.TryGetValue("definitionJson", out StringValues values))
        {
            return null;
        }

        var normalized = values.ToString().Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized;
    }

    private static string BuildSafeSaveValidationMessage(string? statusCode)
    {
        var normalized = statusCode?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "Strateji taslağı oluşturulamadı.";
        }

        if (normalized.StartsWith("InvalidRsiThreshold", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("InvalidRuleWeight", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("InvalidRangeOperand", StringComparison.OrdinalIgnoreCase))
        {
            return "Kaydetme engellendi: eşik veya aralık değeri geçersiz.";
        }

        if (normalized.StartsWith("ConflictingRule", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("DuplicateCondition", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("DuplicateRuleId", StringComparison.OrdinalIgnoreCase))
        {
            return "Kaydetme engellendi: çelişkili veya tekrar eden kural var.";
        }

        if (normalized.StartsWith("MissingRule", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("EmptyRuleGroup", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("MissingRuleRoot", StringComparison.OrdinalIgnoreCase))
        {
            return "Kaydetme engellendi: zorunlu builder alanları eksik.";
        }

        if (normalized.StartsWith("Unsupported", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("OperandTypeMismatch", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("MismatchedRuleType", StringComparison.OrdinalIgnoreCase))
        {
            return "Kaydetme engellendi: kural alanları birbiriyle uyumlu değil.";
        }

        return "Strateji taslağı oluşturulamadı.";
    }

    private static string BuildSafeSaveParseMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Kaydetme engellendi: builder JSON çözümlenemedi.";
        }

        return message.Contains("required", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("must contain", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("must be", StringComparison.OrdinalIgnoreCase)
            ? "Kaydetme engellendi: builder alanlarında eksik veya hatalı veri var."
            : "Kaydetme engellendi: builder JSON çözümlenemedi.";
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
