using System.Diagnostics;
using CoinBot.Infrastructure;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Contracts.Common;
using CoinBot.Web.Hubs;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
    var sqlConnectionStringBuilder = string.IsNullOrWhiteSpace(connectionString)
        ? null
        : new SqlConnectionStringBuilder(connectionString);
    var scannerHandoffEnabled = builder.Configuration.GetValue<bool>("MarketData:Scanner:HandoffEnabled");
    var marketDataRestBaseUrl = builder.Configuration.GetValue<string>("MarketData:Binance:RestBaseUrl") ?? "unknown";
    var marketDataWebSocketBaseUrl = builder.Configuration.GetValue<string>("MarketData:Binance:WebSocketBaseUrl") ?? "unknown";
    var exchangeSyncRestBaseUrl = builder.Configuration.GetValue<string>("ExchangeSync:Binance:RestBaseUrl") ?? "unknown";
    var exchangeSyncWebSocketBaseUrl = builder.Configuration.GetValue<string>("ExchangeSync:Binance:WebSocketBaseUrl") ?? "unknown";
    var contentRootPath = builder.Environment.ContentRootPath;
    var appBaseDirectory = AppContext.BaseDirectory;
    var processPath = Environment.ProcessPath ?? "unknown";

    Log.Information(
        "Runtime configuration resolved. Environment={EnvironmentName} MarketDataBinanceEnabled={MarketDataBinanceEnabled} ScannerHandoffEnabled={ScannerHandoffEnabled} MarketDataRestBaseUrl={MarketDataRestBaseUrl} MarketDataWebSocketBaseUrl={MarketDataWebSocketBaseUrl} ExchangeSyncBinanceEnabled={ExchangeSyncBinanceEnabled} ExchangeSyncRestBaseUrl={ExchangeSyncRestBaseUrl} ExchangeSyncWebSocketBaseUrl={ExchangeSyncWebSocketBaseUrl} ContentRootPath={ContentRootPath} AppBaseDirectory={AppBaseDirectory} ProcessPath={ProcessPath} DbServer={DbServer} DbName={DbName}",
        builder.Environment.EnvironmentName,
        builder.Configuration.GetValue<bool>("MarketData:Binance:Enabled"),
        scannerHandoffEnabled,
        marketDataRestBaseUrl,
        marketDataWebSocketBaseUrl,
        builder.Configuration.GetValue<bool>("ExchangeSync:Binance:Enabled"),
        exchangeSyncRestBaseUrl,
        exchangeSyncWebSocketBaseUrl,
        contentRootPath,
        appBaseDirectory,
        processPath,
        sqlConnectionStringBuilder?.DataSource ?? "unknown",
        sqlConnectionStringBuilder?.InitialCatalog ?? "unknown");

    builder.Host.UseSerilog((context, _, loggerConfiguration) =>
    {
        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentName()
            .Enrich.WithMachineName()
            .Enrich.WithProperty("Application", "CoinBot.Web")
            .Destructure.With<SensitiveDataRedactionPolicy>();
    });

    // Add services to the container.
    builder.Services.AddControllersWithViews();
    builder.Services.AddSignalR();
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddHostedService<MarketDataHubBridgeService>();
    builder.Services.AddHostedService<UserOperationsHubBridgeService>();

    if (builder.Environment.IsDevelopment())
    {
        builder.Services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
            options.ServicesStartConcurrently = true;
        });
    }

    var app = builder.Build();

    try
    {
        await app.Services.EnsureIdentitySeedDataAsync(app.Configuration);
    }
    catch (Exception exception) when (app.Environment.IsDevelopment() && IsStartupDatabaseException(exception))
    {
        Log.Warning(
            exception,
            "Development startup database initialization failed. Web host will continue without automatic identity seed.");
    }

    app.UseMiddleware<CorrelationContextMiddleware>();
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
        options.GetLevel = (httpContext, _, exception) =>
            exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
                ? LogEventLevel.Error
                : httpContext.Response.StatusCode >= StatusCodes.Status400BadRequest
                    ? LogEventLevel.Warning
                    : LogEventLevel.Information;
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            var correlationContext = httpContext.RequestServices
                .GetService<ICorrelationContextAccessor>()
                ?.Current;

            diagnosticContext.Set("RequestId", correlationContext?.RequestId ?? httpContext.TraceIdentifier);
            diagnosticContext.Set("CorrelationId", correlationContext?.CorrelationId ?? httpContext.TraceIdentifier);
            diagnosticContext.Set("TraceId", correlationContext?.TraceId ?? Activity.Current?.TraceId.ToString() ?? string.Empty);
            diagnosticContext.Set("EndpointName", httpContext.GetEndpoint()?.DisplayName ?? "unknown");
            diagnosticContext.Set("Authenticated", httpContext.User.Identity?.IsAuthenticated == true);
        };
    });

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseRouting();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthChecks("/health", CreateHealthCheckOptions(static _ => true))
        .RequireAuthorization(ApplicationPolicies.AdminPortalAccess);
    app.MapHealthChecks("/health/live", CreateHealthCheckOptions(static registration => registration.Tags.Contains("live")));
    app.MapHealthChecks("/health/ready", CreateHealthCheckOptions(static registration => registration.Tags.Contains("ready")))
        .RequireAuthorization(ApplicationPolicies.AdminPortalAccess);
    app.MapHealthChecks("/health/market", CreateHealthCheckOptions(static registration => registration.Tags.Contains("market")))
        .RequireAuthorization(ApplicationPolicies.AdminPortalAccess);
    app.MapHealthChecks("/health/data-latency", CreateHealthCheckOptions(static registration => registration.Tags.Contains("data-latency")))
        .RequireAuthorization(ApplicationPolicies.AdminPortalAccess);
    app.MapHealthChecks("/health/demo-engine", CreateHealthCheckOptions(static registration => registration.Tags.Contains("demo-engine")))
        .RequireAuthorization(ApplicationPolicies.AdminPortalAccess);

    app.MapStaticAssets();

    app.MapAreaControllerRoute(
        name: "super-admin",
        areaName: "Admin",
        pattern: "admin/{action=Overview}/{id?}",
        defaults: new { controller = "Admin" })
        .WithStaticAssets();

    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets();
    app.MapHub<MarketDataHub>("/hubs/market-data");
    app.MapHub<UserOperationsHub>("/hubs/operations");

    app.Run();
}
catch (Exception exception)
{
    Log.Fatal(exception, "CoinBot.Web terminated unexpectedly.");
}
finally
{
    await Log.CloseAndFlushAsync();
}

static HealthCheckOptions CreateHealthCheckOptions(Func<HealthCheckRegistration, bool> predicate)
{
    return new HealthCheckOptions
    {
        Predicate = predicate,
        ResponseWriter = HealthCheckResponseWriter.WriteAsync
    };
}

static bool IsStartupDatabaseException(Exception exception)
{
    for (var current = exception; current is not null; current = current.InnerException)
    {
        if (current is SqlException or DbUpdateException or System.Data.Common.DbException)
        {
            return true;
        }
    }

    return false;
}
