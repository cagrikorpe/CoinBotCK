using CoinBot.Application.Abstractions.Alerts;
using CoinBot.Application.Abstractions.Auditing;
using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Application.Abstractions.DemoPortfolio;
using CoinBot.Application.Abstractions.ExchangeCredentials;
using CoinBot.Application.Abstractions.Exchange;
using CoinBot.Application.Abstractions.Execution;
using CoinBot.Application.Abstractions.Indicators;
using CoinBot.Application.Abstractions.MarketData;
using CoinBot.Contracts.Common;
using CoinBot.Infrastructure.Alerts;
using CoinBot.Infrastructure.Auditing;
using CoinBot.Infrastructure.Credentials;
using CoinBot.Infrastructure.DemoPortfolio;
using CoinBot.Infrastructure.Exchange;
using CoinBot.Infrastructure.Execution;
using CoinBot.Infrastructure.Identity;
using CoinBot.Infrastructure.MarketData;
using CoinBot.Infrastructure.Mfa;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CoinBot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        var configuredCacheOptions = configuration.GetSection("Caching:InMemory").Get<InMemoryCacheOptions>() ?? new InMemoryCacheOptions();
        var memoryCacheSizeLimit = configuredCacheOptions.SizeLimit >= 128
            ? configuredCacheOptions.SizeLimit
            : new InMemoryCacheOptions().SizeLimit;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
        }

        services.AddDataProtection()
            .SetApplicationName("CoinBot");
        services.AddMemoryCache(options =>
        {
            options.SizeLimit = memoryCacheSizeLimit;
        });
        services.AddHttpContextAccessor();
        services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<AlertingOptions>()
            .Bind(configuration.GetSection("Alerting"));
        services.AddOptions<SlackAlertOptions>()
            .Bind(configuration.GetSection("Alerting:Slack"));
        services.AddOptions<TelegramAlertOptions>()
            .Bind(configuration.GetSection("Alerting:Telegram"));
        services.AddOptions<MfaOptions>()
            .Bind(configuration.GetSection("Mfa"))
            .ValidateDataAnnotations();
        services.AddOptions<CredentialSecurityOptions>()
            .Bind(configuration.GetSection("CredentialSecurity"))
            .ValidateDataAnnotations()
            .Validate(
                options => options.RotationIntervalDays >= options.RevalidationIntervalDays,
                "RotationIntervalDays must be greater than or equal to RevalidationIntervalDays.");
        services.AddOptions<MarketHealthOptions>()
            .Bind(configuration.GetSection("HealthMonitoring:Market"))
            .ValidateDataAnnotations();
        services.AddOptions<DataLatencyGuardOptions>()
            .Bind(configuration.GetSection("ExecutionSafety:DataLatencyGuard"))
            .ValidateDataAnnotations()
            .Validate(
                options => options.StopDataThresholdSeconds >= options.StaleDataThresholdSeconds,
                "StopDataThresholdSeconds must be greater than or equal to StaleDataThresholdSeconds.");
        services.AddOptions<InMemoryCacheOptions>()
            .Bind(configuration.GetSection("Caching:InMemory"))
            .ValidateDataAnnotations();
        services.AddOptions<BinanceMarketDataOptions>()
            .Bind(configuration.GetSection("MarketData:Binance"))
            .ValidateDataAnnotations()
            .Validate(
                options => Uri.IsWellFormedUriString(options.RestBaseUrl, UriKind.Absolute),
                "RestBaseUrl must be an absolute URI.")
            .Validate(
                options => Uri.IsWellFormedUriString(options.WebSocketBaseUrl, UriKind.Absolute),
                "WebSocketBaseUrl must be an absolute URI.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.KlineInterval),
                "KlineInterval is required.");
        services.AddOptions<HistoricalGapFillerOptions>()
            .Bind(configuration.GetSection("MarketData:HistoricalGapFiller"))
            .ValidateDataAnnotations();
        services.AddOptions<BinancePrivateDataOptions>()
            .Bind(configuration.GetSection("ExchangeSync:Binance"))
            .ValidateDataAnnotations()
            .Validate(
                options => Uri.IsWellFormedUriString(options.RestBaseUrl, UriKind.Absolute),
                "RestBaseUrl must be an absolute URI.")
            .Validate(
                options => Uri.IsWellFormedUriString(options.WebSocketBaseUrl, UriKind.Absolute),
                "WebSocketBaseUrl must be an absolute URI.")
            .Validate(
                options => options.ListenKeyRenewalIntervalMinutes < 60,
                "ListenKeyRenewalIntervalMinutes must be less than 60.");
        services.AddOptions<IndicatorEngineOptions>()
            .Bind(configuration.GetSection("MarketData:Indicators"))
            .ValidateDataAnnotations()
            .Validate(
                options => options.MacdSlowPeriod > options.MacdFastPeriod,
                "MacdSlowPeriod must be greater than MacdFastPeriod.");
        services.AddScoped<IDataScopeContextAccessor, DataScopeContextAccessor>();
        services.AddScoped<IDataScopeContext>(serviceProvider => serviceProvider.GetRequiredService<IDataScopeContextAccessor>());
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<IExchangeCredentialService, ExchangeCredentialService>();
        services.AddScoped<IAlertService, AlertingService>();
        services.AddScoped<IGlobalExecutionSwitchService, GlobalExecutionSwitchService>();
        services.AddScoped<IDataLatencyCircuitBreaker, DataLatencyCircuitBreaker>();
        services.AddScoped<IDemoPortfolioAccountingService, DemoPortfolioAccountingService>();
        services.AddScoped<IExecutionGate, ExecutionGate>();
        services.AddScoped<TradingModeService>();
        services.AddScoped<ITradingModeResolver>(serviceProvider => serviceProvider.GetRequiredService<TradingModeService>());
        services.AddScoped<ITradingModeService>(serviceProvider => serviceProvider.GetRequiredService<TradingModeService>());
        services.AddScoped<ITotpService, TotpService>();
        services.AddScoped<IEmailOtpService, EmailOtpService>();
        services.AddScoped<IMfaCodeValidator, MfaCodeValidator>();
        services.AddSingleton<ICredentialKeyResolver, CredentialKeyResolver>();
        services.AddSingleton<ICredentialCipher, Aes256CredentialCipher>();
        services.AddSingleton<MarketDataCachePolicyProvider>();
        services.AddSingleton<SharedSymbolRegistry>();
        services.AddSingleton<ISharedSymbolRegistry>(serviceProvider => serviceProvider.GetRequiredService<SharedSymbolRegistry>());
        services.AddSingleton<MarketPriceStreamHub>();
        services.AddSingleton<MarketDataService>();
        services.AddSingleton<IMarketDataService>(serviceProvider => serviceProvider.GetRequiredService<MarketDataService>());
        services.AddSingleton<IndicatorStreamHub>();
        services.AddSingleton<IndicatorDataService>();
        services.AddSingleton<IIndicatorDataService>(serviceProvider => serviceProvider.GetRequiredService<IndicatorDataService>());
        services.AddSingleton<ExchangeAccountSnapshotHub>();
        services.AddSingleton<CandleContinuityValidator>();
        services.AddSingleton<CandleDataQualityGuard>();
        services.AddSingleton<IMarketDataHeartbeatRecorder, MarketDataHeartbeatRecorder>();
        services.AddSingleton<IBinanceCandleStreamClient, BinanceCandleStreamClient>();
        services.AddSingleton<IBinancePrivateStreamClient, BinancePrivateStreamClient>();
        services.AddScoped<HistoricalGapFillerService>();
        services.AddScoped<ExchangeAccountSyncStateService>();
        services.AddScoped<ExchangeBalanceSyncService>();
        services.AddScoped<ExchangePositionSyncService>();
        services.AddScoped<ExchangeAppStateSyncService>();
        services.AddHttpClient<SlackAlertProvider>();
        services.AddHttpClient<TelegramAlertProvider>();
        services.AddHttpClient<IBinanceExchangeInfoClient, BinanceExchangeInfoClient>((serviceProvider, client) =>
        {
            var marketDataOptions = serviceProvider.GetRequiredService<IOptions<BinanceMarketDataOptions>>().Value;
            client.BaseAddress = new Uri(marketDataOptions.RestBaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<IBinanceHistoricalKlineClient, BinanceHistoricalKlineClient>((serviceProvider, client) =>
        {
            var marketDataOptions = serviceProvider.GetRequiredService<IOptions<BinanceMarketDataOptions>>().Value;
            client.BaseAddress = new Uri(marketDataOptions.RestBaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<IBinancePrivateRestClient, BinancePrivateRestClient>((serviceProvider, client) =>
        {
            var privateDataOptions = serviceProvider.GetRequiredService<IOptions<BinancePrivateDataOptions>>().Value;
            client.BaseAddress = new Uri(privateDataOptions.RestBaseUrl, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<IAlertProvider>(serviceProvider => serviceProvider.GetRequiredService<SlackAlertProvider>());
        services.AddScoped<IAlertProvider>(serviceProvider => serviceProvider.GetRequiredService<TelegramAlertProvider>());
        services.AddHostedService<BinanceWebSocketManager>();
        services.AddHostedService<HistoricalGapFillerWorker>();
        services.AddHostedService<BinancePrivateStreamManager>();
        services.AddHostedService<ExchangeBalanceSyncWorker>();
        services.AddHostedService<ExchangePositionSyncWorker>();
        services.AddHostedService<ExchangeAppStateSyncWorker>();

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(connectionString, sqlOptions =>
                sqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));

        services.AddHealthChecks()
            .AddCheck("live", () => HealthCheckResult.Healthy("The service is accepting requests."), tags: ["live"])
            .AddCheck<DatabaseHealthCheck>("db", tags: ["ready", "db"])
            .AddCheck<MarketHealthCheck>("market", tags: ["ready", "market"])
            .AddCheck<DemoEngineHealthCheck>("demo-engine", tags: ["ready", "demo-engine"]);

        services
            .AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequiredUniqueChars = 1;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

                options.User.RequireUniqueEmail = true;
                options.Tokens.AuthenticatorIssuer = "CoinBot";
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Auth/Login";
            options.AccessDeniedPath = "/Auth/AccessDenied";
            options.Cookie.Name = "CoinBot.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToAccessDenied = context =>
                {
                    context.Response.Redirect(
                        context.Request.Path.StartsWithSegments("/admin")
                            ? "/Admin/Admin/AccessDenied"
                            : context.RedirectUri);

                    return Task.CompletedTask;
                }
            };
        });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(ApplicationPolicies.AdminPortalAccess, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole(
                    ApplicationRoles.SuperAdmin,
                    ApplicationRoles.Admin,
                    ApplicationRoles.Support,
                    ApplicationRoles.Auditor);
                policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.AdminPortalAccess);
            });

            options.AddPolicy(ApplicationPolicies.IdentityAdministration, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole(ApplicationRoles.SuperAdmin, ApplicationRoles.Admin);
                policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.IdentityAdministration);
            });

            options.AddPolicy(ApplicationPolicies.TradeOperations, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole(ApplicationRoles.SuperAdmin, ApplicationRoles.Admin, ApplicationRoles.User);
                policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.TradeOperations);
            });

            options.AddPolicy(ApplicationPolicies.RiskManagement, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole(ApplicationRoles.SuperAdmin, ApplicationRoles.Admin, ApplicationRoles.User);
                policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.RiskManagement);
            });

            options.AddPolicy(ApplicationPolicies.ExchangeManagement, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole(ApplicationRoles.SuperAdmin, ApplicationRoles.Admin, ApplicationRoles.User);
                policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.ExchangeManagement);
            });

            options.AddPolicy(ApplicationPolicies.AuditRead, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole(
                    ApplicationRoles.SuperAdmin,
                    ApplicationRoles.Admin,
                    ApplicationRoles.Support,
                    ApplicationRoles.Auditor);
                policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.AuditRead);
            });

            options.AddPolicy(ApplicationPolicies.PlatformAdministration, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireRole(ApplicationRoles.SuperAdmin, ApplicationRoles.Admin);
                policy.RequireClaim(ApplicationClaimTypes.Permission, ApplicationPermissions.PlatformAdministration);
            });
        });

        return services;
    }
}
