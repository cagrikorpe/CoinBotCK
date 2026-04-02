using CoinBot.Application.Abstractions.Bots;
using CoinBot.Domain.Entities;
using CoinBot.Domain.Enums;
using CoinBot.Infrastructure.Dashboard;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure.Jobs;

public sealed class BotPilotControlService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<BotExecutionPilotOptions> options,
    UserOperationsStreamHub? userOperationsStreamHub = null) : IBotPilotControlService
{
    private readonly BotExecutionPilotOptions optionsValue = options.Value;

    public async Task<BotPilotToggleResult> SetEnabledAsync(
        string ownerUserId,
        Guid botId,
        bool isEnabled,
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
            return Failure(botId, isEnabled, "BotNotFound", "Bot bulunamadı.");
        }

        if (bot.IsEnabled == isEnabled)
        {
            return new BotPilotToggleResult(bot.Id, bot.IsEnabled, true, null, null);
        }

        if (isEnabled)
        {
            var symbol = NormalizeSymbol(bot.Symbol);

            if (!IsAllowedSymbol(symbol))
            {
                return Failure(bot.Id, false, "PilotSymbolNotAllowed", "Pilot bot yalnizca izinli futures sembolleri ile etkinlestirilebilir.");
            }

            var sameSymbolEnabledBotExists = (await dbContext.TradingBots
                    .IgnoreQueryFilters()
                    .Where(entity =>
                        entity.Id != bot.Id &&
                        entity.OwnerUserId == bot.OwnerUserId &&
                        entity.IsEnabled &&
                        !entity.IsDeleted)
                    .Select(entity => entity.Symbol)
                    .ToListAsync(cancellationToken))
                .Select(NormalizeSymbol)
                .Any(item => string.Equals(item, symbol, StringComparison.Ordinal));

            if (sameSymbolEnabledBotExists)
            {
                return Failure(bot.Id, false, "PilotSymbolConflictMultipleEnabledBots", "Ayni kullanici icin ayni sembolde yalnizca bir etkin bot olabilir.");
            }

            if (!await HasEligibleExchangeAccountAsync(bot, cancellationToken))
            {
                return Failure(bot.Id, false, "ExchangeAccountSelectionRequired", "Pilot bot icin aktif ve yazilabilir Binance hesabi gerekli.");
            }

            if (!await HasPublishedStrategyVersionAsync(bot, cancellationToken))
            {
                return Failure(bot.Id, false, "PublishedStrategyVersionMissing", "Pilot bot icin yayinlanmis strateji versiyonu gerekli.");
            }
        }

        bot.IsEnabled = isEnabled;
        ApplySchedulerState(bot.Id, isEnabled);
        await dbContext.SaveChangesAsync(cancellationToken);
        userOperationsStreamHub?.Publish(
            new UserOperationsUpdate(
                bot.OwnerUserId,
                "BotStateChanged",
                bot.Id,
                null,
                isEnabled ? "Enabled" : "Disabled",
                null,
                timeProvider.GetUtcNow().UtcDateTime));

        return new BotPilotToggleResult(bot.Id, bot.IsEnabled, true, null, null);
    }

    private void ApplySchedulerState(Guid botId, bool isEnabled)
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

    private async Task<bool> HasEligibleExchangeAccountAsync(TradingBot bot, CancellationToken cancellationToken)
    {
        if (bot.ExchangeAccountId.HasValue)
        {
            return await dbContext.ExchangeAccounts
                .IgnoreQueryFilters()
                .AnyAsync(
                    entity => entity.Id == bot.ExchangeAccountId.Value &&
                              entity.OwnerUserId == bot.OwnerUserId &&
                              !entity.IsDeleted &&
                              entity.ExchangeName == "Binance" &&
                              entity.CredentialStatus == ExchangeCredentialStatus.Active &&
                              !entity.IsReadOnly,
                    cancellationToken);
        }

        var eligibleAccountCount = await dbContext.ExchangeAccounts
            .IgnoreQueryFilters()
            .CountAsync(
                entity => entity.OwnerUserId == bot.OwnerUserId &&
                          !entity.IsDeleted &&
                          entity.ExchangeName == "Binance" &&
                          entity.CredentialStatus == ExchangeCredentialStatus.Active &&
                          !entity.IsReadOnly,
                cancellationToken);

        return eligibleAccountCount == 1;
    }

    private async Task<bool> HasPublishedStrategyVersionAsync(TradingBot bot, CancellationToken cancellationToken)
    {
        var strategy = await dbContext.TradingStrategies
            .IgnoreQueryFilters()
            .Where(entity => entity.OwnerUserId == bot.OwnerUserId &&
                             entity.StrategyKey == bot.StrategyKey &&
                             !entity.IsDeleted)
            .OrderByDescending(entity => entity.UpdatedDate)
            .ThenByDescending(entity => entity.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (strategy is null)
        {
            return false;
        }

        return await dbContext.TradingStrategyVersions
            .IgnoreQueryFilters()
            .AnyAsync(
                entity => entity.TradingStrategyId == strategy.Id &&
                          entity.Status == StrategyVersionStatus.Published &&
                          !entity.IsDeleted,
                cancellationToken);
    }

    private static BotPilotToggleResult Failure(Guid botId, bool isEnabled, string failureCode, string failureReason)
    {
        return new BotPilotToggleResult(botId, isEnabled, false, failureCode, failureReason);
    }

    private bool IsAllowedSymbol(string symbol)
    {
        var allowedSymbols = optionsValue.AllowedSymbols
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        if (allowedSymbols.Count == 0)
        {
            allowedSymbols.Add(NormalizeSymbol(optionsValue.DefaultSymbol));
        }

        return allowedSymbols.Contains(symbol);
    }

    private string NormalizeSymbol(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? optionsValue.DefaultSymbol.Trim().ToUpperInvariant()
            : value.Trim().ToUpperInvariant();
    }
}
