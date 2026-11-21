using Microsoft.Extensions.DependencyInjection.Extensions;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using SqlDataCollector.Export;
using SqlDataCollector.Extract;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Options;
using SqlDataCollector.Scheduling;
using SqlDataCollector.Services;

namespace SqlDataCollector;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the job runners and services. Needed in every process that schedules or executes
    /// SqlDataCollector jobs: our own Web/Worker host, or an existing organization-wide Hangfire Server host.
    /// Reads the "ConnectionStrings:Metadata", "Collector", "Gcs" and "SqlCredentialOverrides" configuration.
    /// Requires Hangfire to be registered by the host (IRecurringJobManager, IBackgroundJobClient).
    /// </summary>
    public static IServiceCollection AddSqlDataCollectorCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CollectorOptions>(configuration.GetSection(CollectorOptions.Section));
        services.Configure<GcsOptions>(configuration.GetSection(GcsOptions.Section));

        services.AddSingleton<SqlConnectionFactory>();
        services.AddSingleton<MetadataRepository>();
        services.AddSingleton<TargetTableManager>();
        services.AddSingleton<MergeLoader>();
        services.AddSingleton<Privacy.MaskingService>();
        services.AddSingleton<Privacy.PrivacyScanner>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<GcsCredentialStore>();
        services.AddSingleton<GcsUploader>();
        services.AddSingleton<CatalogService>();
        services.AddSingleton<JobRegistrar>();
        services.TryAddSingleton<IDependentJobLauncher, HangfireDependentJobLauncher>();
        services.AddSingleton<DependencyService>();
        services.AddSingleton<PermissionChecker>();
        services.AddSingleton<SystemSettingsService>();
        services.Configure<Alerts.AlertOptions>(configuration.GetSection(Alerts.AlertOptions.Section));
        services.AddSingleton<Alerts.AlertSender>();
        services.AddSingleton<Alerts.AlertDispatcher>();
        services.AddSingleton<PermissionCheckRequester>();

        services.AddTransient<ExtractRunner>();
        services.AddTransient<ExportRunner>();
        services.AddTransient<RetentionRunner>();
        services.AddTransient<JobSynchronizer>();
        services.AddTransient<PermissionCheckRunner>();
        return services;
    }

    /// <summary>
    /// On startup: installs/upgrades the etl schema, registers the system recurring jobs (sync, retention)
    /// and, in a Worker, runs the first sync. Register it BEFORE AddHangfireServer.
    /// </summary>
    public static IServiceCollection AddSqlDataCollectorStartup(this IServiceCollection services) =>
        services.AddHostedService<StartupService>();

    /// <summary>
    /// Hangfire SQL Server storage as used by the SqlDataCollector host:
    /// ConnectionStrings:Hangfire (falls back to Metadata = embedded mode), schema Collector:HangfireSchema.
    /// </summary>
    public static IGlobalConfiguration UseSqlDataCollectorStorage(this IGlobalConfiguration hangfire, IConfiguration configuration)
    {
        var collector = configuration.GetSection(CollectorOptions.Section).Get<CollectorOptions>() ?? new();
        var cs = new SqlConnectionFactory(configuration).HangfireConnectionString;

        return hangfire
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(() => new SqlConnection(cs), new SqlServerStorageOptions
            {
                SchemaName = collector.HangfireSchema,
                PrepareSchemaIfNecessary = collector.HangfirePrepareSchema,
                CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                QueuePollInterval = TimeSpan.Zero,
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true
            });
    }
}
