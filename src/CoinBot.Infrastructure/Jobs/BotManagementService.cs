using CoinBot.Application.Abstractions.Bots;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BotManagementService(
    ApplicationDbContext dbContext,
    IBotPilotControlService botPilotControlService,
    TimeProvider timeProvider,
    IOptions<BotExecutionPilotOptions> options,
    UserOperationsStreamHub? userOperationsStreamHub = null) : IBotManagementService
{
    private const string FrozenPilotMarginType = "ISOLATED";
    private readonly BotExecutionPilotOptions optionsValue = options.Value;

    public async Task<BotManagementPageSnapshot> GetPageAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);

        var bots = await dbContext.TradingBots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == ownerUserId && !entity.IsDeleted)
            .OrderBy(entity => entity.Name)
            .ThenBy(entity => entity.Id)
            .ToListAsync(cancellationToken);

        var botIds = bots.Select(entity => entity.Id).ToArray();
        var strategyKeys = bots
            .Select(entity => entity.StrategyKey)
            .Where(entity => !string.IsNullOrWhiteSpace(entity))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var exchangeAccountIds = bots
            .Where(entity => entity.ExchangeAccountId.HasValue)
            .Select(entity => entity.ExchangeAccountId!.Value)
            .Distinct()
            .ToArray();

        var strategies = await dbContext.TradingStrategies
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted &&
                strategyKeys.Contains(entity.StrategyKey))
            .ToListAsync(cancellationToken);

        var strategyIds = strategies.Select(entity => entity.Id).ToArray();
        var publishedStrategyIds = await dbContext.TradingStrategyVersions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                strategyIds.Contains(entity.TradingStrategyId) &&
                entity.Status == StrategyVersionStatus.Published &&
                !entity.IsDeleted)
            .Select(entity => entity.TradingStrategyId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var accounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                !entity.IsDeleted &&
                exchangeAccountIds.Contains(entity.Id))
            .ToListAsync(cancellationToken);

        var states = await dbContext.BackgroundJobStates
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.JobType == BackgroundJobTypes.BotExecution &&
                entity.BotId.HasValue &&
                botIds.Contains(entity.BotId.Value))
            .ToListAsync(cancellationToken);

        var latestOrders = await dbContext.ExecutionOrders
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                !entity.IsDeleted &&
                entity.BotId.HasValue &&
                botIds.Contains(entity.BotId.Value))
            .OrderByDescending(entity => entity.CreatedDate)
            .ToListAsync(cancellationToken);

        var strategiesByKey = strategies.ToDictionary(entity => entity.StrategyKey, StringComparer.Ordinal);
        var publishedStrategyIdSet = publishedStrategyIds.ToHashSet();
        var accountsById = accounts.ToDictionary(entity => entity.Id);
        var statesByBotId = states
            .Where(entity => entity.BotId.HasValue)
            .GroupBy(entity => entity.BotId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(entity => entity.UpdatedDate).First());
        var ordersByBotId = latestOrders
            .Where(entity => entity.BotId.HasValue)
            .GroupBy(entity => entity.BotId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.First());

        var snapshots = bots
            .Select(bot =>
            {
                strategiesByKey.TryGetValue(bot.StrategyKey, out var strategy);
                ExchangeAccount? account = null;

                if (bot.ExchangeAccountId.HasValue)
                {
                    accountsById.TryGetValue(bot.ExchangeAccountId.Value, out account);
                }

                statesByBotId.TryGetValue(bot.Id, out var state);
                ordersByBotId.TryGetValue(bot.Id, out var order);

                return new BotManagementBotSnapshot(
                    bot.Id,
                    bot.Name,
                    bot.StrategyKey,
                    strategy?.DisplayName,
                    strategy is not null && publishedStrategyIdSet.Contains(strategy.Id),
                    NormalizeSymbol(bot.Symbol),
                    bot.Quantity,
                    bot.ExchangeAccountId,
                    account?.DisplayName,
                    account?.CredentialStatus == ExchangeCredentialStatus.Active,
                    account is not null && !account.IsReadOnly,
                    bot.Leverage,
                    NormalizeMarginType(bot.MarginType),
                    bot.IsEnabled,
                    bot.OpenOrderCount,
                    bot.OpenPositionCount,
                    state?.Status.ToString(),
                    state?.LastErrorCode,
                    order?.State.ToString(),
                    order?.FailureCode,
                    order?.UpdatedDate,
                    bot.UpdatedDate);
            })
            .ToArray();

        return new BotManagementPageSnapshot(snapshots);
    }

    public async Task<BotManagementEditorSnapshot> GetCreateEditorAsync(string ownerUserId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);

        var draft = new BotManagementDraftSnapshot(
            Name: string.Empty,
            StrategyKey: string.Empty,
            Symbol: NormalizeSymbol(optionsValue.DefaultSymbol),
            Quantity: null,
            ExchangeAccountId: await ResolveSingleActiveExchangeAccountIdAsync(ownerUserId, cancellationToken),
            Leverage: 1m,
            MarginType: FrozenPilotMarginType,
            IsEnabled: false);

        return await CreateEditorSnapshotAsync(ownerUserId, null, draft, cancellationToken);
    }

    public async Task<BotManagementEditorSnapshot?> GetEditEditorAsync(string ownerUserId, Guid botId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);

        var bot = await dbContext.TradingBots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.Id == botId &&
                          entity.OwnerUserId == ownerUserId &&
                          !entity.IsDeleted,
                cancellationToken);

        if (bot is null)
        {
            return null;
        }

        var draft = new BotManagementDraftSnapshot(
            bot.Name,
            bot.StrategyKey,
            NormalizeSymbol(bot.Symbol),
            bot.Quantity,
            bot.ExchangeAccountId,
            bot.Leverage ?? 1m,
            NormalizeMarginType(bot.MarginType),
            bot.IsEnabled);

        return await CreateEditorSnapshotAsync(ownerUserId, bot.Id, draft, cancellationToken);
    }

    public async Task<BotManagementSaveResult> CreateAsync(
        string ownerUserId,
        BotManagementSaveCommand command,
        string actor,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var validationError = await ValidateCommandAsync(ownerUserId, null, command, cancellationToken);

        if (validationError is not null)
        {
            return validationError;
        }

        var bot = new TradingBot
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = NormalizeRequired(command.Name, nameof(command.Name)),
            StrategyKey = NormalizeRequired(command.StrategyKey, nameof(command.StrategyKey)),
            Symbol = NormalizeSymbol(command.Symbol),
            Quantity = NormalizeQuantity(command.Quantity),
            ExchangeAccountId = command.ExchangeAccountId,
            Leverage = NormalizeLeverage(command.Leverage),
            MarginType = NormalizeMarginType(command.MarginType),
            IsEnabled = false
        };

        dbContext.TradingBots.Add(bot);
        await dbContext.SaveChangesAsync(cancellationToken);
        userOperationsStreamHub?.Publish(
            new UserOperationsUpdate(
                ownerUserId,
                "BotChanged",
                bot.Id,
                null,
                "Created",
                null,
                timeProvider.GetUtcNow().UtcDateTime));

        return await ApplyEnabledStateAsync(ownerUserId, bot, command.IsEnabled, actor, correlationId, cancellationToken);
    }

    public async Task<BotManagementSaveResult> UpdateAsync(
        string ownerUserId,
        Guid botId,
        BotManagementSaveCommand command,
        string actor,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var bot = await dbContext.TradingBots
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                entity => entity.Id == botId &&
                          entity.OwnerUserId == ownerUserId &&
                          !entity.IsDeleted,
                cancellationToken);

        if (bot is null)
        {
            return new BotManagementSaveResult(null, false, false, false, "BotNotFound", "Bot bulunamadı.");
        }

        var validationError = await ValidateCommandAsync(ownerUserId, botId, command, cancellationToken);

        if (validationError is not null)
        {
            return validationError with { BotId = botId };
        }

        var wasEnabled = bot.IsEnabled;
        bot.Name = NormalizeRequired(command.Name, nameof(command.Name));
        bot.StrategyKey = NormalizeRequired(command.StrategyKey, nameof(command.StrategyKey));
        bot.Symbol = NormalizeSymbol(command.Symbol);
        bot.Quantity = NormalizeQuantity(command.Quantity);
        bot.ExchangeAccountId = command.ExchangeAccountId;
        bot.Leverage = NormalizeLeverage(command.Leverage);
        bot.MarginType = NormalizeMarginType(command.MarginType);

        if (command.IsEnabled)
        {
            bot.IsEnabled = false;
            await dbContext.SaveChangesAsync(cancellationToken);
            return await ApplyEnabledStateAsync(ownerUserId, bot, true, actor, correlationId, cancellationToken);
        }

        if (wasEnabled)
        {
            var toggleResult = await botPilotControlService.SetEnabledAsync(
                ownerUserId,
                bot.Id,
                false,
                actor,
                correlationId,
                cancellationToken);

            return new BotManagementSaveResult(
                bot.Id,
                toggleResult.IsSuccessful,
                true,
                false,
                toggleResult.FailureCode,
                toggleResult.FailureReason);
        }

        ResetSchedulerState(bot.Id, isEnabled: false);
        await dbContext.SaveChangesAsync(cancellationToken);
        userOperationsStreamHub?.Publish(
            new UserOperationsUpdate(
                ownerUserId,
                "BotChanged",
                bot.Id,
                null,
                "Updated",
                null,
                timeProvider.GetUtcNow().UtcDateTime));

        return new BotManagementSaveResult(bot.Id, true, true, false, null, null);
    }

    private async Task<BotManagementSaveResult> ApplyEnabledStateAsync(
        string ownerUserId,
        TradingBot bot,
        bool shouldEnable,
        string actor,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (!shouldEnable)
        {
            ResetSchedulerState(bot.Id, isEnabled: false);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new BotManagementSaveResult(bot.Id, true, true, false, null, null);
        }

        var toggleResult = await botPilotControlService.SetEnabledAsync(
            ownerUserId,
            bot.Id,
            true,
            actor,
            correlationId,
            cancellationToken);

        return new BotManagementSaveResult(
            bot.Id,
            toggleResult.IsSuccessful,
            true,
            toggleResult.IsEnabled,
            toggleResult.FailureCode,
            toggleResult.FailureReason);
    }

    private async Task<BotManagementSaveResult?> ValidateCommandAsync(
        string ownerUserId,
        Guid? currentBotId,
        BotManagementSaveCommand command,
        CancellationToken cancellationToken)
    {
        var name = command.Name?.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "NameRequired", "Bot adı zorunludur.");
        }

        var strategyKey = command.StrategyKey?.Trim();

        if (string.IsNullOrWhiteSpace(strategyKey))
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "StrategyKeyRequired", "Bot için strateji seçimi zorunludur.");
        }

        var strategyExists = await dbContext.TradingStrategies
            .IgnoreQueryFilters()
            .AnyAsync(
                entity => entity.OwnerUserId == ownerUserId &&
                          entity.StrategyKey == strategyKey &&
                          !entity.IsDeleted,
                cancellationToken);

        if (!strategyExists)
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "StrategyNotFound", "Seçilen strateji bulunamadı.");
        }

        var symbol = NormalizeSymbol(command.Symbol);

        if (!IsAllowedSymbol(symbol))
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "PilotSymbolNotAllowed", "Pilot bot yalnizca izinli futures sembolleri ile kaydedilebilir.");
        }

        if (command.Quantity is decimal quantity && quantity <= 0m)
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "QuantityMustBePositive", "Quantity pozitif olmalıdır.");
        }

        var leverage = NormalizeLeverage(command.Leverage);

        if (leverage != 1m)
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "PilotLeverageMustBeOne", "Pilot bot yalnızca 1x leverage ile kaydedilebilir.");
        }

        var marginType = NormalizeMarginType(command.MarginType);

        if (!string.Equals(marginType, FrozenPilotMarginType, StringComparison.Ordinal))
        {
            return new BotManagementSaveResult(currentBotId, false, false, false, "PilotMarginTypeMustBeIsolated", "Pilot bot yalnızca ISOLATED margin ile kaydedilebilir.");
        }

        if (command.ExchangeAccountId.HasValue)
        {
            var accountExists = await dbContext.ExchangeAccounts
                .IgnoreQueryFilters()
                .AnyAsync(
                    entity => entity.Id == command.ExchangeAccountId.Value &&
                              entity.OwnerUserId == ownerUserId &&
                              entity.ExchangeName == "Binance" &&
                              !entity.IsDeleted,
                    cancellationToken);

            if (!accountExists)
            {
                return new BotManagementSaveResult(currentBotId, false, false, false, "ExchangeAccountNotFound", "Seçilen Binance hesabı bulunamadı.");
            }
        }

        return null;
    }

    private async Task<BotManagementEditorSnapshot> CreateEditorSnapshotAsync(
        string ownerUserId,
        Guid? botId,
        BotManagementDraftSnapshot draft,
        CancellationToken cancellationToken)
    {
        var strategies = await dbContext.TradingStrategies
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == ownerUserId && !entity.IsDeleted)
            .OrderBy(entity => entity.DisplayName)
            .ThenBy(entity => entity.StrategyKey)
            .ToListAsync(cancellationToken);

        var strategyIds = strategies.Select(entity => entity.Id).ToArray();
        var publishedStrategyIds = await dbContext.TradingStrategyVersions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                strategyIds.Contains(entity.TradingStrategyId) &&
                entity.Status == StrategyVersionStatus.Published &&
                !entity.IsDeleted)
            .Select(entity => entity.TradingStrategyId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var publishedStrategyIdSet = publishedStrategyIds.ToHashSet();

        var strategyOptions = strategies
            .Select(entity => new BotStrategyOptionSnapshot(
                entity.StrategyKey,
                entity.DisplayName,
                publishedStrategyIdSet.Contains(entity.Id)))
            .ToArray();

        var exchangeAccounts = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.ExchangeName == "Binance" &&
                !entity.IsDeleted)
            .OrderBy(entity => entity.DisplayName)
            .ThenBy(entity => entity.Id)
            .ToListAsync(cancellationToken);

        var exchangeAccountOptions = exchangeAccounts
            .Select(entity => new BotExchangeAccountOptionSnapshot(
                entity.Id,
                entity.DisplayName,
                entity.CredentialStatus == ExchangeCredentialStatus.Active,
                !entity.IsReadOnly))
            .ToArray();

        return new BotManagementEditorSnapshot(botId, draft, ResolveSymbolOptions(draft.Symbol), strategyOptions, exchangeAccountOptions);
    }

    private async Task<Guid?> ResolveSingleActiveExchangeAccountIdAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        var accountIds = await dbContext.ExchangeAccounts
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(entity =>
                entity.OwnerUserId == ownerUserId &&
                entity.ExchangeName == "Binance" &&
                entity.CredentialStatus == ExchangeCredentialStatus.Active &&
                !entity.IsReadOnly &&
                !entity.IsDeleted)
            .OrderBy(entity => entity.Id)
            .Select(entity => entity.Id)
            .ToListAsync(cancellationToken);

        return accountIds.Count == 1
            ? accountIds[0]
            : null;
    }

    private void ResetSchedulerState(Guid botId, bool isEnabled)
    {
        var state = dbContext.BackgroundJobStates
            .IgnoreQueryFilters()
            .SingleOrDefault(entity =>
                entity.BotId == botId &&
                entity.JobType == BackgroundJobTypes.BotExecution &&
                !entity.IsDeleted);

        if (state is null || state.Status == BackgroundJobStatus.Running)
        {
            return;
        }

        if (isEnabled)
        {
            var utcNow = timeProvider.GetUtcNow().UtcDateTime;
            state.Status = BackgroundJobStatus.Pending;
            state.NextRunAtUtc = utcNow;
            state.LastErrorCode = null;
            state.IdempotencyKey = null;
            return;
        }

        state.Status = BackgroundJobStatus.Failed;
        state.NextRunAtUtc = DateTime.MaxValue;
        state.LastErrorCode = "BotDisabled";
        state.IdempotencyKey = null;
    }

    private static decimal? NormalizeQuantity(decimal? quantity)
    {
        return quantity is null
            ? null
            : decimal.Round(quantity.Value, 18, MidpointRounding.AwayFromZero);
    }

    private static decimal NormalizeLeverage(decimal? leverage)
    {
        return leverage ?? 1m;
    }

    private string NormalizeSymbol(string? symbol)
    {
        return string.IsNullOrWhiteSpace(symbol)
            ? optionsValue.DefaultSymbol.Trim().ToUpperInvariant()
            : symbol.Trim().ToUpperInvariant();
    }

    private static string NormalizeMarginType(string? marginType)
    {
        return string.IsNullOrWhiteSpace(marginType)
            ? FrozenPilotMarginType
            : marginType.Trim().ToUpperInvariant();
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalizedValue = value?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        return normalizedValue;
    }

    private bool IsAllowedSymbol(string symbol)
    {
        return ResolveSymbolOptions(symbol).Contains(symbol, StringComparer.Ordinal);
    }

    private string[] ResolveSymbolOptions(string? currentSymbol = null)
    {
        var symbols = optionsValue.AllowedSymbols
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToUpperInvariant())
            .ToList();

        if (symbols.Count == 0)
        {
            symbols.Add(NormalizeSymbol(optionsValue.DefaultSymbol));
        }

        var normalizedCurrentSymbol = string.IsNullOrWhiteSpace(currentSymbol)
            ? null
            : currentSymbol.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(normalizedCurrentSymbol) &&
            !symbols.Contains(normalizedCurrentSymbol, StringComparer.Ordinal))
        {
            symbols.Add(normalizedCurrentSymbol);
        }

        return symbols
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }
}
