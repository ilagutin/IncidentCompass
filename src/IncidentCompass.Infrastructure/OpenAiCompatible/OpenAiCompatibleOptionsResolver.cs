using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.OpenAiCompatible;

internal static class OpenAiCompatibleOptionsResolver
{
    public static TOptions Get<TOptions>(
        IOptions<TOptions> options,
        Func<OptionsValidationException, Exception> createInvalidConfigurationException)
        where TOptions : class
    {
        try
        {
            return options.Value;
        }
        catch (OptionsValidationException exception)
        {
            throw createInvalidConfigurationException(exception);
        }
    }
}
