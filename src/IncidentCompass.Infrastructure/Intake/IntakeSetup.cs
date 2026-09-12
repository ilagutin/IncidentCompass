using IncidentCompass.Application.Intake.Artifacts;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.FaultGrouping;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IncidentCompass.Infrastructure.Intake;

internal static class IntakeSetup
{
    public static IServiceCollection AddIntakeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TriageConfigSourceOptions>(configuration.GetSection(TriageConfigSourceOptions.SectionName));

        // Real hosts (WebApplication.CreateBuilder / Host.CreateApplicationBuilder) already
        // register a concrete IHostEnvironment before this method runs, so this TryAddSingleton
        // never overrides it; it only backstops raw ServiceCollection compositions.
        services.TryAddSingleton<IHostEnvironment, FallbackHostEnvironment>();

        // The load validator checks that a multi-provider configuration's credentials resolve, so
        // the secret reader is registered here as well as beside the gateway adapters. A
        // composition that validates a configuration without composing any model adapter - the
        // `config validate` command path - reaches this registration and not that one.
        services.TryAddSingleton<IModelProviderSecretReader, EnvironmentModelProviderSecretReader>();
        services.TryAddSingleton<TriageConfigurationLoadValidator>();
        services.TryAddSingleton<TriageConfigurationMaterializer>();
        services.TryAddSingleton<TriageConfigurationSnapshotStore>();
        services.TryAddSingleton<ITriageConfigurationSnapshotStore>(
            serviceProvider => serviceProvider.GetRequiredService<TriageConfigurationSnapshotStore>());
        services.TryAddSingleton<FileTriageConfigurationRepository>();
        services.TryAddSingleton<ITriageConfigurationRepository>(
            serviceProvider => serviceProvider.GetRequiredService<FileTriageConfigurationRepository>());

        services.TryAddSingleton<PostgresTriageJobLeaseStore>();
        services.TryAddScoped<PostgresTriageJobAttemptFailureStore>();
        services.TryAddScoped<PostgresIntakeTransactionContext>();
        services.TryAddScoped<IIntakeUnitOfWork, PostgresIntakeUnitOfWork>();
        services.TryAddScoped<ISignalRepository, PostgresSignalRepository>();
        services.TryAddScoped<IFaultRepository, PostgresFaultRepository>();
        services.TryAddScoped<ITriageJobRepository, PostgresTriageJobRepository>();
        services.TryAddScoped<IRecurrenceStateRepository, PostgresRecurrenceStateRepository>();
        services.Replace(ServiceDescriptor.Scoped<IRecurrenceEscalationReTriageScheduler, PostgresRecurrenceEscalationReTriageScheduler>());
        services.TryAddScoped<ITriageJobRuntimeRepository, PostgresTriageJobRuntimeRepository>();
        services.TryAddScoped<ITriageArtifactRepository, PostgresTriageArtifactRepository>();
        services.TryAddScoped<IPriorReportSummaryProvider, PostgresPriorReportSummaryProvider>();

        services.AddHostedService<TriageConfigurationWarmupHostedService>();

        return services;
    }
}

