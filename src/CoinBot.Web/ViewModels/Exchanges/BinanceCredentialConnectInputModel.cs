using System.ComponentModel.DataAnnotations;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Domain.Enums;

namespace CoinBot.Web.ViewModels.Exchanges;

public sealed class BinanceCredentialConnectInputModel
{
    public Guid? ExchangeAccountId { get; set; }

    [Required(ErrorMessage = "API key gerekli.")]
    [StringLength(256)]
    public string ApiKey { get; set; } = string.Empty;

    [Required(ErrorMessage = "API secret gerekli.")]
    [StringLength(256)]
    public string ApiSecret { get; set; } = string.Empty;

    [Required]
    public ExecutionEnvironment RequestedEnvironment { get; set; } = ExecutionEnvironment.Demo;

    [Required]
    public ExchangeTradeModeSelection RequestedTradeMode { get; set; } = ExchangeTradeModeSelection.Spot;
}
