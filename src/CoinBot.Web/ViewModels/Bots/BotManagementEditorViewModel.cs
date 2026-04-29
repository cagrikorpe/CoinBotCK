using System.ComponentModel.DataAnnotations;
using CoinBot.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CoinBot.Web.ViewModels.Bots;

public sealed class BotManagementEditorViewModel
{
    public BotManagementEditorViewModel(
        Guid? botId,
        bool isEditMode,
        BotManagementInputModel form,
        IReadOnlyCollection<BotManagementOptionViewModel> symbolOptions,
        IReadOnlyCollection<string> scannerUniverseSymbols,
        IReadOnlyCollection<BotManagementOptionViewModel> strategyOptions,
        IReadOnlyCollection<BotManagementOptionViewModel> exchangeAccountOptions)
    {
        BotId = botId;
        IsEditMode = isEditMode;
        Form = form;
        SymbolOptions = symbolOptions;
        ScannerUniverseSymbols = scannerUniverseSymbols;
        StrategyOptions = strategyOptions;
        ExchangeAccountOptions = exchangeAccountOptions;
    }

    public Guid? BotId { get; }

    public bool IsEditMode { get; }

    public BotManagementInputModel Form { get; }

    public IReadOnlyCollection<BotManagementOptionViewModel> SymbolOptions { get; }

    public IReadOnlyCollection<string> ScannerUniverseSymbols { get; }

    public IReadOnlyCollection<BotManagementOptionViewModel> StrategyOptions { get; }

    public IReadOnlyCollection<BotManagementOptionViewModel> ExchangeAccountOptions { get; }
}

public sealed class BotManagementInputModel
{
    [Required(ErrorMessage = "Bot adı zorunludur.")]
    [StringLength(128)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Strateji seçimi zorunludur.")]
    [StringLength(128)]
    public string StrategyKey { get; set; } = string.Empty;

    [Required(ErrorMessage = "Pilot sembol zorunludur.")]
    [StringLength(32)]
    public string Symbol { get; set; } = "BTCUSDT";

    [BindNever]
    public IReadOnlyCollection<string> ScannerUniverseSymbols { get; set; } = Array.Empty<string>();

    public List<string> AllowedSymbols { get; set; } = [];

    [Range(typeof(decimal), "0.000000000000000001", "1000000000000000000", ErrorMessage = "Quantity pozitif olmalıdır.")]
    public decimal? Quantity { get; set; }

    public Guid? ExchangeAccountId { get; set; }

    public decimal? Leverage { get; set; } = 1m;

    [Required(ErrorMessage = "Margin type zorunludur.")]
    [StringLength(32)]
    public string MarginType { get; set; } = "ISOLATED";

    [Required(ErrorMessage = "Bot yön modu zorunludur.")]
    public TradingBotDirectionMode DirectionMode { get; set; } = TradingBotDirectionMode.LongOnly;

    public bool IsEnabled { get; set; }
}

public sealed record BotManagementOptionViewModel(
    string Value,
    string Label,
    bool IsReady = true);
