using System.ComponentModel.DataAnnotations;

namespace CoinBot.Web.ViewModels.Auth;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "E-posta veya kullanıcı adı zorunludur.")]
    public string EmailOrUserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Şifre zorunludur.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}
