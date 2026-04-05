using CoinBot.Application.Abstractions.Administration;
using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.Autonomy;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Application.Abstractions.Mfa;
using CoinBot.Application.Abstractions.Monitoring;
using CoinBot.Application.Abstractions.Policy;
using CoinBot.Application.Abstractions.Risk;
using CoinBot.Application.Abstractions.Strategies;
using CoinBot.Application.Abstractions.Settings;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure;
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Autonomy;
using CoinBot.Infrastructure.Credentials;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Monitoring;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Policy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
        var userExchangeCommandCenterService = provider.GetRequiredService<IUserExchangeCommandCenterService>();
        var globalExecutionSwitchService = provider.GetRequiredService<IGlobalExecutionSwitchService>();
        var globalSystemStateService = provider.GetRequiredService<IGlobalSystemStateService>();
        var adminAuditLogService = provider.GetRequiredService<IAdminAuditLogService>();
        var adminCommandRegistry = provider.GetRequiredService<IAdminCommandRegistry>();
        var adminShellReadModelService = provider.GetRequiredService<IAdminShellReadModelService>();
        var adminMonitoringReadModelService = provider.GetRequiredService<IAdminMonitoringReadModelService>();
        var apiCredentialValidationService = provider.GetRequiredService<IApiCredentialValidationService>();
        var dataLatencyCircuitBreaker = provider.GetRequiredService<IDataLatencyCircuitBreaker>();
        var demoPortfolioAccountingService = provider.GetRequiredService<IDemoPortfolioAccountingService>();
        var spotPortfolioAccountingService = provider.GetRequiredService<ISpotPortfolioAccountingService>();
        var demoSessionService = provider.GetRequiredService<IDemoSessionService>();
        var demoWalletValuationService = provider.GetRequiredService<DemoWalletValuationService>();
        var demoConsistencyWatchdogService = provider.GetRequiredService<DemoConsistencyWatchdogService>();
        var demoFillSimulator = provider.GetRequiredService<DemoFillSimulator>();
        var executionOrderLifecycleService = provider.GetRequiredService<ExecutionOrderLifecycleService>();
        var executionReconciliationService = provider.GetRequiredService<ExecutionReconciliationService>();
        var virtualExecutionWatchdogService = provider.GetRequiredService<VirtualExecutionWatchdogService>();
        var binanceSpotExecutor = provider.GetRequiredService<BinanceSpotExecutor>();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        var healthCheckService = provider.GetRequiredService<HealthCheckService>();
        var healthCheckOptions = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var tradingModeResolver = provider.GetRequiredService<ITradingModeResolver>();
        var tradingModeService = provider.GetRequiredService<ITradingModeService>();
        var identityOptions = provider.GetRequiredService<IOptions<IdentityOptions>>().Value;
        var marketHealthOptions = provider.GetRequiredService<IOptions<MarketHealthOptions>>().Value;
        var dataLatencyOptions = provider.GetRequiredService<IOptions<DataLatencyGuardOptions>>().Value;
        var demoFillOptions = provider.GetRequiredService<IOptions<DemoFillSimulatorOptions>>().Value;
        var demoSessionOptions = provider.GetRequiredService<IOptions<DemoSessionOptions>>().Value;
        var inMemoryCacheOptions = provider.GetRequiredService<IOptions<InMemoryCacheOptions>>().Value;
        var binanceMarketDataOptions = provider.GetRequiredService<IOptions<BinanceMarketDataOptions>>().Value;
        var historicalGapFillerOptions = provider.GetRequiredService<IOptions<HistoricalGapFillerOptions>>().Value;
        var privateDataOptions = provider.GetRequiredService<IOptions<BinancePrivateDataOptions>>().Value;
        var indicatorOptions = provider.GetRequiredService<IOptions<IndicatorEngineOptions>>().Value;
        var mfaOptions = provider.GetRequiredService<IOptions<MfaOptions>>().Value;
        var credentialSecurityOptions = provider.GetRequiredService<IOptions<CredentialSecurityOptions>>().Value;
        var autonomyOptions = provider.GetRequiredService<IOptions<AutonomyOptions>>().Value;
        var marketAnomalyOptions = provider.GetRequiredService<IOptions<MarketAnomalyOptions>>().Value;
        var dependencyCircuitBreakerOptions = provider.GetRequiredService<IOptions<DependencyCircuitBreakerOptions>>().Value;
        var cookieOptions = provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var antiforgeryOptions = provider.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        var executionGate = provider.GetRequiredService<IExecutionGate>();
        var executionEngine = provider.GetRequiredService<IExecutionEngine>();
        var traceService = provider.GetRequiredService<ITraceService>();
        var userExecutionOverrideGuard = provider.GetRequiredService<IUserExecutionOverrideGuard>();
        var globalPolicyEngine = provider.GetRequiredService<IGlobalPolicyEngine>();
        var riskPolicyEvaluator = provider.GetRequiredService<IRiskPolicyEvaluator>();
        var autonomyReviewQueueService = provider.GetRequiredService<IAutonomyReviewQueueService>();
        var autonomyService = provider.GetRequiredService<IAutonomyService>();
        var breakerStateManager = provider.GetRequiredService<IDependencyCircuitBreakerStateManager>();
        var selfHealingExecutor = provider.GetRequiredService<ISelfHealingExecutor>();
        var marketAnomalyService = provider.GetRequiredService<MarketAnomalyService>();
        var webSocketReconnectCoordinator = provider.GetRequiredService<IWebSocketReconnectCoordinator>();
        var signalRReconnectCoordinator = provider.GetRequiredService<ISignalRReconnectCoordinator>();
        var marketDataService = provider.GetRequiredService<IMarketDataService>();
        var indicatorDataService = provider.GetRequiredService<IIndicatorDataService>();
        var strategyEvaluatorService = provider.GetRequiredService<IStrategyEvaluatorService>();
        var strategyRuleParser = provider.GetRequiredService<IStrategyRuleParser>();
        var strategySignalService = provider.GetRequiredService<IStrategySignalService>();
        var strategyVersionService = provider.GetRequiredService<IStrategyVersionService>();
        var sharedSymbolRegistry = provider.GetRequiredService<ISharedSymbolRegistry>();
        var redisLatencyProbe = provider.GetRequiredService<IRedisLatencyProbe>();
        var candleContinuityValidator = provider.GetRequiredService<CandleContinuityValidator>();
        var candleDataQualityGuard = provider.GetRequiredService<CandleDataQualityGuard>();
        var candleStreamClient = provider.GetRequiredService<IBinanceCandleStreamClient>();
        var historicalKlineClient = provider.GetRequiredService<IBinanceHistoricalKlineClient>();
        var historicalGapFillerService = provider.GetRequiredService<HistoricalGapFillerService>();
        var exchangeAccountSnapshotHub = provider.GetRequiredService<ExchangeAccountSnapshotHub>();
        var timeSyncService = provider.GetRequiredService<IBinanceTimeSyncService>();
        var privateRestClient = provider.GetRequiredService<IBinancePrivateRestClient>();
        var spotPrivateRestClient = provider.GetRequiredService<IBinanceSpotPrivateRestClient>();
        var privateStreamClient = provider.GetRequiredService<IBinancePrivateStreamClient>();
        var spotPrivateStreamClient = provider.GetRequiredService<IBinanceSpotPrivateStreamClient>();
        var spotTimeSyncService = provider.GetRequiredService<IBinanceSpotTimeSyncService>();
        var monitoringTelemetryCollector = provider.GetRequiredService<IMonitoringTelemetryCollector>();
        var exchangeAccountSyncStateService = provider.GetRequiredService<ExchangeAccountSyncStateService>();
        var exchangeBalanceSyncService = provider.GetRequiredService<ExchangeBalanceSyncService>();
        var exchangePositionSyncService = provider.GetRequiredService<ExchangePositionSyncService>();
        var exchangeAppStateSyncService = provider.GetRequiredService<ExchangeAppStateSyncService>();
        var spotExchangeAppStateSyncService = provider.GetRequiredService<SpotExchangeAppStateSyncService>();
        var userSettingsService = provider.GetRequiredService<IUserSettingsService>();
        var totpService = provider.GetRequiredService<ITotpService>();
        var emailOtpService = provider.GetRequiredService<IEmailOtpService>();
        var mfaCodeValidator = provider.GetRequiredService<IMfaCodeValidator>();
        var mfaManagementService = provider.GetRequiredService<IMfaManagementService>();
        var criticalUserOperationAuthorizer = provider.GetRequiredService<ICriticalUserOperationAuthorizer>();

        Assert.Same(dataScopeContextAccessor, dataScopeContext);
        Assert.True(identityOptions.User.RequireUniqueEmail);
        Assert.Equal(8, identityOptions.Password.RequiredLength);
        Assert.True(identityOptions.Lockout.AllowedForNewUsers);
        Assert.Equal(15, marketHealthOptions.ValidationFreshnessMinutes);
        Assert.Equal(3, dataLatencyOptions.StaleDataThresholdSeconds);
        Assert.Equal(6, dataLatencyOptions.StopDataThresholdSeconds);
        Assert.Equal(2, dataLatencyOptions.ClockDriftThresholdSeconds);
        Assert.Equal(8, demoFillOptions.MakerFeeBps);
        Assert.Equal(10, demoFillOptions.TakerFeeBps);
        Assert.Equal(6, demoFillOptions.MarketOrderBaseSlippageBps);
        Assert.Equal(0.60m, demoFillOptions.PartialFillRatio);
        Assert.Equal(10000m, demoFillOptions.PartialFillMinNotional);
        Assert.Equal("USDT", demoSessionOptions.DefaultSeedAsset);
        Assert.Equal(10000m, demoSessionOptions.DefaultSeedAmount);
        Assert.Equal(0.00000001m, demoSessionOptions.ConsistencyTolerance);
        Assert.Equal(2048, inMemoryCacheOptions.SizeLimit);
        Assert.Equal(60, inMemoryCacheOptions.SymbolMetadataTtlMinutes);
        Assert.Equal(15, inMemoryCacheOptions.LatestPriceTtlSeconds);
        Assert.False(binanceMarketDataOptions.Enabled);
        Assert.Equal("https://fapi.binance.com", binanceMarketDataOptions.RestBaseUrl);
        Assert.Equal("wss://fstream.binance.com", binanceMarketDataOptions.WebSocketBaseUrl);
        Assert.Equal("1m", binanceMarketDataOptions.KlineInterval);
        Assert.False(historicalGapFillerOptions.Enabled);
        Assert.Equal(5, historicalGapFillerOptions.ScanIntervalMinutes);
        Assert.Equal(1440, historicalGapFillerOptions.LookbackCandles);
        Assert.Equal(500, historicalGapFillerOptions.MaxCandlesPerRequest);
        Assert.Equal(3, historicalGapFillerOptions.MaxRetryAttempts);
        Assert.Equal(2, historicalGapFillerOptions.RetryDelaySeconds);
        Assert.False(privateDataOptions.Enabled);
        Assert.Equal("https://fapi.binance.com", privateDataOptions.RestBaseUrl);
        Assert.Equal("wss://fstream.binance.com", privateDataOptions.WebSocketBaseUrl);
        Assert.Equal("https://api.binance.com", privateDataOptions.SpotRestBaseUrl);
        Assert.Equal("wss://stream.binance.com:9443", privateDataOptions.SpotWebSocketBaseUrl);
        Assert.Equal(30, privateDataOptions.ListenKeyRenewalIntervalMinutes);
        Assert.Equal(5, privateDataOptions.ReconciliationIntervalMinutes);
        Assert.Equal(30, privateDataOptions.ServerTimeSyncRefreshSeconds);
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
        Assert.Equal(CookieSecurePolicy.Always, cookieOptions.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, cookieOptions.Cookie.SameSite);
        Assert.Equal("CoinBot.Antiforgery", antiforgeryOptions.Cookie.Name);
        Assert.True(antiforgeryOptions.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, antiforgeryOptions.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, antiforgeryOptions.Cookie.SameSite);
        Assert.Equal(6, mfaOptions.EmailOtpCodeLength);
        Assert.Equal(10, mfaOptions.EmailOtpLifetimeMinutes);
        Assert.Equal(30, mfaOptions.TotpTimeStepSeconds);
        Assert.Equal(15, autonomyOptions.SelfHealingIntervalSeconds);
        Assert.Equal(0.85m, autonomyOptions.DefaultConfidenceScore);
        Assert.Equal(0.35m, autonomyOptions.MaxFalsePositiveProbability);
        Assert.Equal(30, autonomyOptions.ReviewQueueTtlMinutes);
        Assert.Equal(30, marketAnomalyOptions.SweepIntervalSeconds);
        Assert.Equal(20, marketAnomalyOptions.MinimumHistoricalCandles);
        Assert.Equal(0.06m, marketAnomalyOptions.PriceShockSeverePercent);
        Assert.Equal(12, marketAnomalyOptions.StaleDataCriticalSeconds);
        Assert.Equal(3, dependencyCircuitBreakerOptions.FailureThreshold);
        Assert.Equal(60, dependencyCircuitBreakerOptions.CooldownSeconds);
        Assert.Equal(30, dependencyCircuitBreakerOptions.ReviewQueueTtlMinutes);
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
        Assert.NotNull(userExchangeCommandCenterService);
        Assert.NotNull(globalExecutionSwitchService);
        Assert.NotNull(globalSystemStateService);
        Assert.NotNull(adminAuditLogService);
        Assert.NotNull(adminCommandRegistry);
        Assert.NotNull(adminShellReadModelService);
        Assert.NotNull(adminMonitoringReadModelService);
        Assert.NotNull(apiCredentialValidationService);
        Assert.NotNull(dataLatencyCircuitBreaker);
        Assert.NotNull(demoPortfolioAccountingService);
        Assert.NotNull(spotPortfolioAccountingService);
        Assert.NotNull(demoSessionService);
        Assert.NotNull(demoWalletValuationService);
        Assert.NotNull(demoConsistencyWatchdogService);
        Assert.NotNull(demoFillSimulator);
        Assert.NotNull(executionOrderLifecycleService);
        Assert.NotNull(executionReconciliationService);
        Assert.NotNull(virtualExecutionWatchdogService);
        Assert.NotNull(binanceSpotExecutor);
        Assert.NotNull(healthCheckService);
        Assert.Contains(healthCheckOptions.Registrations, registration => registration.Name == "data-latency");
        Assert.NotNull(tradingModeResolver);
        Assert.NotNull(tradingModeService);
        Assert.NotNull(executionGate);
        Assert.NotNull(executionEngine);
        Assert.NotNull(traceService);
        Assert.NotNull(userExecutionOverrideGuard);
        Assert.NotNull(globalPolicyEngine);
        Assert.NotNull(riskPolicyEvaluator);
        Assert.NotNull(autonomyReviewQueueService);
        Assert.NotNull(autonomyService);
        Assert.NotNull(breakerStateManager);
        Assert.NotNull(selfHealingExecutor);
        Assert.NotNull(marketAnomalyService);
        Assert.NotNull(webSocketReconnectCoordinator);
        Assert.NotNull(signalRReconnectCoordinator);
        Assert.NotNull(marketDataService);
        Assert.NotNull(indicatorDataService);
        Assert.NotNull(strategyEvaluatorService);
        Assert.NotNull(strategyRuleParser);
        Assert.NotNull(strategySignalService);
        Assert.NotNull(strategyVersionService);
        Assert.NotNull(sharedSymbolRegistry);
        Assert.NotNull(redisLatencyProbe);
        Assert.NotNull(candleContinuityValidator);
        Assert.NotNull(candleDataQualityGuard);
        Assert.NotNull(candleStreamClient);
        Assert.NotNull(historicalKlineClient);
        Assert.NotNull(historicalGapFillerService);
        Assert.NotNull(exchangeAccountSnapshotHub);
        Assert.NotNull(timeSyncService);
        Assert.NotNull(spotTimeSyncService);
        Assert.NotNull(privateRestClient);
        Assert.NotNull(spotPrivateRestClient);
        Assert.NotNull(privateStreamClient);
        Assert.NotNull(spotPrivateStreamClient);
        Assert.NotNull(monitoringTelemetryCollector);
        Assert.NotNull(exchangeAccountSyncStateService);
        Assert.NotNull(exchangeBalanceSyncService);
        Assert.NotNull(exchangePositionSyncService);
        Assert.NotNull(exchangeAppStateSyncService);
        Assert.NotNull(spotExchangeAppStateSyncService);
        Assert.NotNull(userSettingsService);
        Assert.NotNull(totpService);
        Assert.NotNull(emailOtpService);
        Assert.NotNull(mfaCodeValidator);
        Assert.NotNull(mfaManagementService);
        Assert.NotNull(criticalUserOperationAuthorizer);
        Assert.Contains(hostedServices, service => service is VirtualExecutionWatchdogWorker);
        Assert.Contains(hostedServices, service => service is MonitoringSnapshotWorker);
        Assert.Contains(hostedServices, service => service is AutonomySelfHealingWorker);
        Assert.Contains(hostedServices, service => service is MarketAnomalyWorker);
        Assert.Contains(hostedServices, service => service is BinanceSpotPrivateStreamManager);
        Assert.Contains(hostedServices, service => service is SpotExchangeAppStateSyncWorker);
    }

    [Fact]
    public async Task AddInfrastructure_ConfiguresAdminAccessDeniedRedirect_ToAdminRoute()
    {
        var provider = BuildServiceProvider();
        var cookieOptions = provider
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/admin/users";
        var redirectContext = new RedirectContext<CookieAuthenticationOptions>(
            httpContext,
            new AuthenticationScheme(IdentityConstants.ApplicationScheme, IdentityConstants.ApplicationScheme, typeof(CookieAuthenticationHandler)),
            cookieOptions,
            new AuthenticationProperties(),
            "/Auth/AccessDenied");

        await cookieOptions.Events.OnRedirectToAccessDenied(redirectContext);

        Assert.Equal("/Admin/Admin/AccessDenied", httpContext.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData(ApplicationPolicies.AdminPortalAccess, ApplicationPermissions.AdminPortalAccess, ApplicationRoles.SuperAdmin, ApplicationRoles.SecurityAuditor)]
    [InlineData(ApplicationPolicies.IdentityAdministration, ApplicationPermissions.IdentityAdministration, ApplicationRoles.SuperAdmin, ApplicationRoles.OpsAdmin)]
    [InlineData(ApplicationPolicies.TradeOperations, ApplicationPermissions.TradeOperations, ApplicationRoles.User, ApplicationRoles.OpsAdmin)]
    [InlineData(ApplicationPolicies.RiskManagement, ApplicationPermissions.RiskManagement, ApplicationRoles.User, ApplicationRoles.OpsAdmin)]
    [InlineData(ApplicationPolicies.ExchangeManagement, ApplicationPermissions.ExchangeManagement, ApplicationRoles.User, ApplicationRoles.OpsAdmin)]
    [InlineData(ApplicationPolicies.AuditRead, ApplicationPermissions.AuditRead, ApplicationRoles.OpsAdmin, ApplicationRoles.SecurityAuditor)]
    [InlineData(ApplicationPolicies.PlatformAdministration, ApplicationPermissions.PlatformAdministration, ApplicationRoles.SuperAdmin, ApplicationRoles.SuperAdmin)]
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

    [Fact]
    public void ApplicationRoleClaims_AssignsReadOnlyPermissionsToSecurityAuditor_AndNoPlatformAdministrationToOpsAdmin()
    {
        var securityAuditorPermissions = ApplicationRoleClaims.GetPermissions(ApplicationRoles.SecurityAuditor);
        var opsAdminPermissions = ApplicationRoleClaims.GetPermissions(ApplicationRoles.OpsAdmin);

        Assert.Contains(ApplicationPermissions.AdminPortalAccess, securityAuditorPermissions);
        Assert.Contains(ApplicationPermissions.AuditRead, securityAuditorPermissions);
        Assert.DoesNotContain(ApplicationPermissions.TradeOperations, securityAuditorPermissions);
        Assert.Contains(ApplicationPermissions.TradeOperations, opsAdminPermissions);
        Assert.DoesNotContain(ApplicationPermissions.PlatformAdministration, opsAdminPermissions);
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
