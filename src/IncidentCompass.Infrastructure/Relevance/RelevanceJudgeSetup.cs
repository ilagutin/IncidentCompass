using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using IncidentCompass.Infrastructure.Relevance.Mock;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Relevance;

internal static class RelevanceJudgeSetup
{
    /// <summary>
    /// The relevance judge the Worker runs, chosen by <c>IncidentCompass:RelevanceJudge:Provider</c>.
    /// <para>
    /// The provider is read here, at composition, because the two judges bring different services:
    /// the local judge brings its install pass and its runtime, and the mock brings neither, so a mock
    /// host downloads nothing whatever judge directory it inherits. The value is validated again at
    /// start, so a misspelled provider stops the Worker with a named failure instead of quietly
    /// composing the local judge. A mock host also logs, once at start, that it runs the mock.
    /// </para>
    /// </summary>
    public static IServiceCollection AddRelevanceJudge(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<RelevanceJudgeOptions>()
            .Bind(configuration.GetSection(RelevanceJudgeOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<RelevanceJudgeOptions>,
            RelevanceJudgeOptionsValidator>());

        if (RelevanceJudgeOptionsValidator.IsMock(configuration[RelevanceJudgeOptions.ProviderKey]))
        {
            services.TryAddSingleton<IMemoryRelevanceJudge, MockMemoryRelevanceJudge>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, MockRelevanceJudgeStartupWarning>());
            return services;
        }

        return services.AddLocalOnnxRelevanceJudge(configuration);
    }
}
