using CoinBot.Application.Abstractions.DataScope;
using CoinBot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CoinBot.Infrastructure.Jobs;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJobOrchestration(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
        }

        services.AddOptions<JobOrchestrationOptions>()
            .Bind(configuration.GetSection("JobOrchestration"))
            .ValidateDataAnnotations()
            .Validate(
                options => options.KeepAliveIntervalSeconds < options.LeaseDurationSeconds,
                "KeepAliveIntervalSeconds must be less than LeaseDurationSeconds.")
            .Validate(
                options => options.WatchdogTimeoutSeconds > options.LeaseDurationSeconds,
                "WatchdogTimeoutSeconds must be greater than LeaseDurationSeconds.")
            .Validate(
                options => options.MaxRetryDelaySeconds >= options.InitialRetryDelaySeconds,
                "MaxRetryDelaySeconds must be greater than or equal to InitialRetryDelaySeconds.")
            .ValidateOnStart();
        services.AddOptions<BotExecutionPilotOptions>()
            .Bind(configuration.GetSection("BotExecutionPilot"))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.PostConfigure<BotExecutionPilotOptions>(options => options.NormalizeScopeCollections());

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDataScopeContext, SystemDataScopeContext>();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(connectionString, sqlOptions =>
                sqlOptions.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));
        services.AddSingleton<ActiveBackgroundJobRegistry>();
        services.AddSingleton<IWorkerInstanceAccessor, WorkerInstanceAccessor>();
        services.AddScoped<IDistributedJobLockManager, DistributedJobLockManager>();
        services.AddScoped<IBotWorkerJobProcessor, BotWorkerJobProcessor>();
        services.AddScoped<BotJobSchedulerService>();
        services.AddScoped<BackgroundJobKeepAliveService>();
        services.AddScoped<BackgroundJobCleanupService>();
        services.AddScoped<BackgroundJobWatchdogService>();

        return services;
    }

    private sealed class SystemDataScopeContext : IDataScopeContext
    {
        public string? UserId => null;

        public bool HasIsolationBypass => true;
    }
}
