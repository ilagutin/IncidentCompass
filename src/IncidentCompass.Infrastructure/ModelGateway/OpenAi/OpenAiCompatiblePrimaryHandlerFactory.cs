using IncidentCompass.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

/// <summary>
/// Builds the socket handler under the OpenAI-compatible chat client. The connect limit belongs
/// here because only the handler knows where the connection phase ends; the first-output and
/// inactivity limits stay in <see cref="OpenAiCompatibleModelClient" />, per HTTP attempt.
/// </summary>
internal static class OpenAiCompatiblePrimaryHandlerFactory
{
    public static HttpMessageHandler Create(IServiceProvider serviceProvider)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(ResolveConnectTimeoutSeconds(serviceProvider))
        };
    }

    /// <summary>
    /// The configured connect limit, or the default when the options are invalid. Invalid options
    /// are reported by the client on its first call as a configuration error, which a handler that
    /// threw while being built could not do.
    /// </summary>
    private static int ResolveConnectTimeoutSeconds(IServiceProvider serviceProvider)
    {
        try
        {
            var options = serviceProvider.GetRequiredService<IOptions<OpenAiCompatibleModelClientOptions>>().Value;
            return options.ConnectTimeoutSeconds is > 0 and <= OpenAiCompatibleModelClientOptions.MaximumConnectTimeoutSeconds
                ? options.ConnectTimeoutSeconds
                : OpenAiCompatibleModelClientOptions.DefaultConnectTimeoutSeconds;
        }
        catch (OptionsValidationException)
        {
            return OpenAiCompatibleModelClientOptions.DefaultConnectTimeoutSeconds;
        }
    }
}
