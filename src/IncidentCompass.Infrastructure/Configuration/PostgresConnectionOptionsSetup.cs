using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Configuration;

internal static class PostgresConnectionOptionsSetup
{
    /// <summary>
    /// Registers the two-step PostgreSQL connection binding in one place:
    /// <see cref="PostgresOptions"/> names the connection string and
    /// <see cref="PostgresConnectionOptions"/> carries the resolved
    /// <c>ConnectionStrings:&lt;name&gt;</c> value. The <see cref="IConfiguration"/> is captured
    /// here instead of being registered as a service, so adapters depend on typed options only.
    /// The startup-retry budget's rules live in <see cref="PostgresStartupRetryOptionsValidator"/>,
    /// registered here beside the binding it validates and enforced by <c>ValidateOnStart</c>.
    /// </summary>
    public static IServiceCollection AddPostgresConnectionOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<PostgresOptions>()
            .Bind(configuration.GetSection(PostgresOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<PostgresOptions>,
            PostgresStartupRetryOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IConfigureOptions<PostgresConnectionOptions>,
            PostgresConnectionOptionsConfigurator>(
            serviceProvider => new PostgresConnectionOptionsConfigurator(
                configuration,
                serviceProvider.GetRequiredService<IOptions<PostgresOptions>>())));

        return services;
    }
}
