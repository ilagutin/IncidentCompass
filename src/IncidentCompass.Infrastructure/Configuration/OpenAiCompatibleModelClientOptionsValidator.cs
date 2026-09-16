using IncidentCompass.Application.Core.ModelGateway;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Configuration;

/// <summary>
/// Validates the OpenAI-compatible chat gateway's call limits while the host starts, and warns once
/// when the first-output limit still comes from the deprecated <c>TimeoutSeconds</c> key.
/// </summary>
/// <remarks>
/// Options validation runs once, when the options instance is first built, which with
/// <c>ValidateOnStart</c> is host start. That makes it the one start-up path that already sees these
/// options, so the deprecation warning lives here rather than in a hosted service of its own, and
/// outside the model gateway adapter folder, whose sources reach no output sink. Only setting names
/// and a number of seconds are logged.
/// </remarks>
internal sealed partial class OpenAiCompatibleModelClientOptionsValidator(
    IOptions<ModelGatewayOptions> modelGatewayOptions,
    ILogger<OpenAiCompatibleModelClientOptionsValidator> logger)
    : IValidateOptions<OpenAiCompatibleModelClientOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenAiCompatibleModelClientOptions options)
    {
        if (!ProviderKindParser.IsOpenAiCompatible(modelGatewayOptions.Value.Provider))
        {
            return ValidateOptionsResult.Success;
        }

        if (options.HasConflictingFirstOutputTimeouts)
        {
            return ValidateOptionsResult.Fail(
                "OpenAI-compatible model gateway sets both FirstOutputTimeoutSeconds and the deprecated " +
                "TimeoutSeconds; set only FirstOutputTimeoutSeconds.");
        }

        if (options.UsesDeprecatedTimeoutSeconds)
        {
            LogDeprecatedTimeoutSeconds(logger, options.ResolveFirstOutputTimeoutSeconds());
        }

        return ValidateOptionsResult.Success;
    }

    [LoggerMessage(
        EventId = 2801,
        Level = LogLevel.Warning,
        Message = "IncidentCompass:ModelGateway:OpenAiCompatible:TimeoutSeconds is deprecated; its value {FirstOutputTimeoutSeconds} is used as the first-output limit. Rename it to FirstOutputTimeoutSeconds.")]
    private static partial void LogDeprecatedTimeoutSeconds(ILogger logger, int firstOutputTimeoutSeconds);
}
