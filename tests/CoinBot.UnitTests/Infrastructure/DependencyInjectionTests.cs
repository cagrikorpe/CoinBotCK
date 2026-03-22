using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure;
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Credentials;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Infrastructure.Observability;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CoinBot.UnitTests.Infrastructure;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddInfrastructure_ConfiguresIdentityCookieAlertingLatencyGuardAndMarketDataOptions()
    {
        var provider = BuildServiceProvider();

        var dataScopeContextAccessor = provider.GetRequiredService<IDataScopeContextAccessor>();
        var dataScopeContext = provider.GetRequiredService<IDataScopeContext>();
        var auditLogService = provider.GetRequiredService<IAuditLogService>();
        var alertService = provider.GetRequiredService<IAlertService>();
        var alertProviders = provider.GetServices<IAlertProvider>().ToArray();
        var correlationContextAccessor = provider.GetRequiredService<ICorrelationContextAccessor>();
        var exchangeCredentialService = provider.GetRequiredService<IExchangeCredentialService>();
        var globalExecutionSwitchService = provider.GetRequiredService<IGlobalExecutionSwitchService>();
        var dataLatencyCircuitBreaker = provider.GetRequiredService<IDataLatencyCircuitBreaker>();
        var healthCheckService = provider.GetRequiredService<HealthCheckService>();
        var tradingModeResolver = provider.GetRequiredService<ITradingModeResolver>();
        var tradingModeService = provider.GetRequiredService<ITradingModeService>();
        var identityOptions = provider.GetRequiredService<IOptions<IdentityOptions>>().Value;
        var marketHealthOptions = provider.GetRequiredService<IOptions<MarketHealthOptions>>().Value;
        var dataLatencyOptions = provider.GetRequiredService<IOptions<DataLatencyGuardOptions>>().Value;
        var inMemoryCacheOptions = provider.GetRequiredService<IOptions<InMemoryCacheOptions>>().Value;
        var binanceMarketDataOptions = provider.GetRequiredService<IOptions<BinanceMarketDataOptions>>().Value;
        var historicalGapFillerOptions = provider.GetRequiredService<IOptions<HistoricalGapFillerOptions>>().Value;
        var indicatorOptions = provider.GetRequiredService<IOptions<IndicatorEngineOptions>>().Value;
        var mfaOptions = provider.GetRequiredService<IOptions<MfaOptions>>().Value;
        var credentialSecurityOptions = provider.GetRequiredService<IOptions<CredentialSecurityOptions>>().Value;
        var cookieOptions = provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var executionGate = provider.GetRequiredService<IExecutionGate>();
        var marketDataService = provider.GetRequiredService<IMarketDataService>();
        var indicatorDataService = provider.GetRequiredService<IIndicatorDataService>();
        var sharedSymbolRegistry = provider.GetRequiredService<ISharedSymbolRegistry>();
        var candleContinuityValidator = provider.GetRequiredService<CandleContinuityValidator>();
        var candleDataQualityGuard = provider.GetRequiredService<CandleDataQualityGuard>();
        var candleStreamClient = provider.GetRequiredService<IBinanceCandleStreamClient>();
        var historicalKlineClient = provider.GetRequiredService<IBinanceHistoricalKlineClient>();
        var historicalGapFillerService = provider.GetRequiredService<HistoricalGapFillerService>();
        var totpService = provider.GetRequiredService<ITotpService>();
        var emailOtpService = provider.GetRequiredService<IEmailOtpService>();
        var mfaCodeValidator = provider.GetRequiredService<IMfaCodeValidator>();

        Assert.Same(dataScopeContextAccessor, dataScopeContext);
        Assert.True(identityOptions.User.RequireUniqueEmail);
        Assert.Equal(8, identityOptions.Password.RequiredLength);
        Assert.True(identityOptions.Lockout.AllowedForNewUsers);
        Assert.Equal(15, marketHealthOptions.ValidationFreshnessMinutes);
        Assert.Equal(3, dataLatencyOptions.StaleDataThresholdSeconds);
        Assert.Equal(6, dataLatencyOptions.StopDataThresholdSeconds);
        Assert.Equal(2, dataLatencyOptions.ClockDriftThresholdSeconds);
        Assert.Equal(2048, inMemoryCacheOptions.SizeLimit);
        Assert.Equal(60, inMemoryCacheOptions.SymbolMetadataTtlMinutes);
        Assert.Equal(15, inMemoryCacheOptions.LatestPriceTtlSeconds);
        Assert.False(binanceMarketDataOptions.Enabled);
        Assert.Equal("https://api.binance.com", binanceMarketDataOptions.RestBaseUrl);
        Assert.Equal("wss://stream.binance.com:9443", binanceMarketDataOptions.WebSocketBaseUrl);
        Assert.Equal("1m", binanceMarketDataOptions.KlineInterval);
        Assert.False(historicalGapFillerOptions.Enabled);
        Assert.Equal(5, historicalGapFillerOptions.ScanIntervalMinutes);
        Assert.Equal(1440, historicalGapFillerOptions.LookbackCandles);
        Assert.Equal(500, historicalGapFillerOptions.MaxCandlesPerRequest);
        Assert.Equal(3, historicalGapFillerOptions.MaxRetryAttempts);
        Assert.Equal(2, historicalGapFillerOptions.RetryDelaySeconds);
        Assert.Equal(14, indicatorOptions.RsiPeriod);
        Assert.Equal(12, indicatorOptions.MacdFastPeriod);
        Assert.Equal(26, indicatorOptions.MacdSlowPeriod);
        Assert.Equal(9, indicatorOptions.MacdSignalPeriod);
        Assert.Equal(20, indicatorOptions.BollingerPeriod);
        Assert.Equal(2m, indicatorOptions.BollingerStandardDeviationMultiplier);
        Assert.Equal("/Auth/Login", cookieOptions.LoginPath);
        Assert.Equal("/Auth/AccessDenied", cookieOptions.AccessDeniedPath);
        Assert.True(cookieOptions.Cookie.HttpOnly);
        Assert.Equal("CoinBot.Auth", cookieOptions.Cookie.Name);
        Assert.Equal(6, mfaOptions.EmailOtpCodeLength);
        Assert.Equal(10, mfaOptions.EmailOtpLifetimeMinutes);
        Assert.Equal(30, mfaOptions.TotpTimeStepSeconds);
        Assert.Equal(CredentialSecurityKeyProvider.Environment, credentialSecurityOptions.Provider);
        Assert.Equal("credential-v1", credentialSecurityOptions.KeyVersion);
        Assert.Equal("COINBOT_CREDENTIAL_ENCRYPTION_KEY_BASE64", credentialSecurityOptions.EnvironmentVariableName);
        Assert.Equal(30, credentialSecurityOptions.RevalidationIntervalDays);
        Assert.Equal(90, credentialSecurityOptions.RotationIntervalDays);
        Assert.Equal(2, alertProviders.Length);
        Assert.NotNull(auditLogService);
        Assert.NotNull(alertService);
        Assert.NotNull(correlationContextAccessor);
        Assert.NotNull(exchangeCredentialService);
        Assert.NotNull(globalExecutionSwitchService);
        Assert.NotNull(dataLatencyCircuitBreaker);
        Assert.NotNull(healthCheckService);
        Assert.NotNull(tradingModeResolver);
        Assert.NotNull(tradingModeService);
        Assert.NotNull(executionGate);
        Assert.NotNull(marketDataService);
        Assert.NotNull(indicatorDataService);
        Assert.NotNull(sharedSymbolRegistry);
        Assert.NotNull(candleContinuityValidator);
        Assert.NotNull(candleDataQualityGuard);
        Assert.NotNull(candleStreamClient);
        Assert.NotNull(historicalKlineClient);
        Assert.NotNull(historicalGapFillerService);
        Assert.NotNull(totpService);
        Assert.NotNull(emailOtpService);
        Assert.NotNull(mfaCodeValidator);
    }

    [Theory]
    [InlineData(ApplicationPolicies.AdminPortalAccess, ApplicationPermissions.AdminPortalAccess, ApplicationRoles.SuperAdmin, ApplicationRoles.Support)]
    [InlineData(ApplicationPolicies.IdentityAdministration, ApplicationPermissions.IdentityAdministration, ApplicationRoles.SuperAdmin, ApplicationRoles.Admin)]
    [InlineData(ApplicationPolicies.TradeOperations, ApplicationPermissions.TradeOperations, ApplicationRoles.User, ApplicationRoles.Admin)]
    [InlineData(ApplicationPolicies.RiskManagement, ApplicationPermissions.RiskManagement, ApplicationRoles.User, ApplicationRoles.Admin)]
    [InlineData(ApplicationPolicies.ExchangeManagement, ApplicationPermissions.ExchangeManagement, ApplicationRoles.User, ApplicationRoles.Admin)]
    [InlineData(ApplicationPolicies.AuditRead, ApplicationPermissions.AuditRead, ApplicationRoles.Support, ApplicationRoles.Auditor)]
    [InlineData(ApplicationPolicies.PlatformAdministration, ApplicationPermissions.PlatformAdministration, ApplicationRoles.SuperAdmin, ApplicationRoles.Admin)]
    public void AddInfrastructure_RegistersExpectedPolicies(string policyName, string permission, string firstRole, string secondRole)
    {
        var provider = BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        var policy = options.GetPolicy(policyName);

        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);

        var claimRequirement = Assert.Single(policy.Requirements.OfType<ClaimsAuthorizationRequirement>());
        var roleRequirement = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());

        Assert.Equal(ApplicationClaimTypes.Permission, claimRequirement.ClaimType);
        Assert.Contains(permission, claimRequirement.AllowedValues!);
        Assert.Contains(firstRole, roleRequirement.AllowedRoles);
        Assert.Contains(secondRole, roleRequirement.AllowedRoles);
    }

    [Fact]
    public void ApplicationRoleClaims_AssignsTradePermissionsToUserRole_AndNoTradePermissionsToSupportRole()
    {
        var userPermissions = ApplicationRoleClaims.GetPermissions(ApplicationRoles.User);
        var supportPermissions = ApplicationRoleClaims.GetPermissions(ApplicationRoles.Support);

        Assert.Contains(ApplicationPermissions.TradeOperations, userPermissions);
        Assert.Contains(ApplicationPermissions.ExchangeManagement, userPermissions);
        Assert.Contains(ApplicationPermissions.RiskManagement, userPermissions);
        Assert.DoesNotContain(ApplicationPermissions.TradeOperations, supportPermissions);
        Assert.Contains(ApplicationPermissions.AuditRead, supportPermissions);
    }

    private static ServiceProvider BuildServiceProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Server=(localdb)\\MSSQLLocalDB;Database=CoinBotTests;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }
}
