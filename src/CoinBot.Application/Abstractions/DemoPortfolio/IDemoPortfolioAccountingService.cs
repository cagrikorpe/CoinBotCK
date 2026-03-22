namespace CoinBot.Application.Abstractions.DemoPortfolio;

public interface IDemoPortfolioAccountingService
{
    Task<DemoPortfolioAccountingResult> SeedWalletAsync(
        DemoWalletSeedRequest request,
        CancellationToken cancellationToken = default);

    Task<DemoPortfolioAccountingResult> ReserveFundsAsync(
        DemoFundsReservationRequest request,
        CancellationToken cancellationToken = default);

    Task<DemoPortfolioAccountingResult> ReleaseFundsAsync(
        DemoFundsReleaseRequest request,
        CancellationToken cancellationToken = default);

    Task<DemoPortfolioAccountingResult> ApplyFillAsync(
        DemoFillAccountingRequest request,
        CancellationToken cancellationToken = default);

    Task<DemoPortfolioAccountingResult> UpdateMarkPriceAsync(
        DemoMarkPriceUpdateRequest request,
        CancellationToken cancellationToken = default);
}
