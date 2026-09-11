using FluentValidation;
using IncidentCompass.Application.Core.Configuration;
using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Health;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Observability;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Core.Users;
using IncidentCompass.Application.Governance;
using IncidentCompass.Application.Intake;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Redaction;
using IncidentCompass.Application.Investigation;
using IncidentCompass.Application.Memory;
using IncidentCompass.Application.Notifications;
using IncidentCompass.Application.Observability.CostRollup;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Application.SourceContext;
using IncidentCompass.Application.Tickets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Application;

public static class Setup
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddApplicationOptions(configuration);
        services.AddValidatorsFromAssembly(typeof(Setup).Assembly, includeInternalTypes: true);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRuntimeTelemetry, RuntimeTelemetry>();
        services
            .AddOptions<ProviderResilienceOptions>()
            .Bind(configuration.GetSection(ProviderResilienceOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ProviderResilienceOptions>, ProviderResilienceOptionsValidator>());
        services.TryAddSingleton<IProviderOutageTracker, ProviderOutageTracker>();
        services.TryAddScoped<IApplicationDispatcher, ApplicationDispatcher>();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(DispatchLoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(RequestValidationBehavior<,>));
        services.AddHealthCore();
        services.AddUsersCore();
        services.AddIntakeCore();
        services.AddGovernanceCore();
        services.AddInvestigationCore();
        services.AddMemoryCore();
        services.AddRemediationCore();
        services.AddSourceContextCore();
        services.AddTicketsCore();
        services.TryAddScoped<IRequestHandler<CostRollupQuery, CostRollupResponse>, CostRollupQueryHandler>();
        services.AddSingleton(TelegramNotificationToolDescriptor.Value);

        return services;
    }

    private static IServiceCollection AddApplicationOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<ApplicationOptions>()
            .Bind(configuration.GetSection(ApplicationOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<ApplicationOptions>,
            ApplicationOptionsValidator>());

        services
            .AddOptions<ModelGatewayOptions>()
            .Bind(configuration.GetSection(ModelGatewayOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<ModelGatewayOptions>,
            ModelGatewayOptionsValidator>());

        services
            .AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<EmbeddingOptions>,
            EmbeddingOptionsValidator>());

        services
            .AddOptions<IngestionLimitsOptions>()
            .Bind(configuration.GetSection(IngestionLimitsOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<IngestionLimitsOptions>,
            IngestionLimitsOptionsValidator>());

        services
            .AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<RetentionOptions>,
            RetentionOptionsValidator>());

        services
            .AddOptions<PseudonymizationOptions>()
            .Bind(configuration.GetSection(PseudonymizationOptions.SectionName));

        return services;
    }
}
