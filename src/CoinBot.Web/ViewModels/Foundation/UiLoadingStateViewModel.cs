namespace CoinBot.Web.ViewModels.Foundation;

public sealed class UiLoadingStateViewModel
{
    public string Title { get; init; } = "Yükleniyor";
    public string Message { get; init; } = "İçerik hazırlanıyor.";
    public int CardCount { get; init; } = 3;
    public int LineCount { get; init; } = 4;
}
