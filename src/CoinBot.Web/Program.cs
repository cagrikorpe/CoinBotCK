using System.Diagnostics;
using CoinBot.Infrastructure;
using CoinBot.Infrastructure.Observability;
using CoinBot.Infrastructure.Persistence;
using CoinBot.Web.Hubs;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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

    var app = builder.Build();

    await app.Services.EnsureIdentitySeedDataAsync(app.Configuration);

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
    app.UseRouting();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthChecks("/health", CreateHealthCheckOptions(static _ => true));
    app.MapHealthChecks("/health/live", CreateHealthCheckOptions(static registration => registration.Tags.Contains("live")));
    app.MapHealthChecks("/health/ready", CreateHealthCheckOptions(static registration => registration.Tags.Contains("ready")));
    app.MapHealthChecks("/health/market", CreateHealthCheckOptions(static registration => registration.Tags.Contains("market")));
    app.MapHealthChecks("/health/data-latency", CreateHealthCheckOptions(static registration => registration.Tags.Contains("data-latency")));
    app.MapHealthChecks("/health/demo-engine", CreateHealthCheckOptions(static registration => registration.Tags.Contains("demo-engine")));

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
