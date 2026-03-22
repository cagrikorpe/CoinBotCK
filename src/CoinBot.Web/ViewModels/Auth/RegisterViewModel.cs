using System.ComponentModel.DataAnnotations;

namespace CoinBot.Web.ViewModels.Auth;

public sealed class RegisterViewModel
{
    [Required(ErrorMessage = "Ad Soyad zorunludur.")]
    [StringLength(200, ErrorMessage = "Ad Soyad en fazla 200 karakter olabilir.")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "E-posta zorunludur.")]
    [EmailAddress(ErrorMessage = "Geçerli bir e-posta adresi girin.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Şifre zorunludur.")]
    [DataType(DataType.Password)]
    [RegularExpression(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z\d]).{8,}$", ErrorMessage = "Şifre en az 8 karakter olmalı; büyük harf, küçük harf, rakam ve sembol içermelidir.")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Şifre tekrar zorunludur.")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "Şifre ve şifre tekrar alanları eşleşmiyor.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Range(typeof(bool), "true", "true", ErrorMessage = "Devam etmek için risk onayını kabul etmelisiniz.")]
    public bool AcceptRiskDisclosure { get; set; }
}
