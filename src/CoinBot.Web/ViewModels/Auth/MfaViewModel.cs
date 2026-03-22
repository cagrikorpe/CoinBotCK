using System.ComponentModel.DataAnnotations;

namespace CoinBot.Web.ViewModels.Auth;

public sealed class MfaViewModel
{
    [Required(ErrorMessage = "Doğrulama kodu zorunludur.")]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "6 haneli doğrulama kodu girin.")]
    public string Code { get; set; } = string.Empty;

    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}
