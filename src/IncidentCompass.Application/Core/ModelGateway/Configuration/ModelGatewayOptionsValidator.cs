using Microsoft.Extensions.Options;

namespace IncidentCompass.Application.Core.ModelGateway;

internal sealed class ModelGatewayOptionsValidator : IValidateOptions<ModelGatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, ModelGatewayOptions options)
    {
        var valid =
            !string.IsNullOrWhiteSpace(options.Provider) &&
            !double.IsNaN(options.MinTemperature) &&
            !double.IsInfinity(options.MinTemperature) &&
            !double.IsNaN(options.MaxTemperature) &&
            !double.IsInfinity(options.MaxTemperature) &&
            options.MinTemperature <= options.MaxTemperature &&
            !double.IsNaN(options.DefaultTemperature) &&
            !double.IsInfinity(options.DefaultTemperature) &&
            options.DefaultTemperature >= options.MinTemperature &&
            options.DefaultTemperature <= options.MaxTemperature &&
            options.MaxInputMessageCharacters > 0 &&
            options.MaxOutputTokensLimit > 0 &&
            options.DefaultMaxOutputTokens is > 0 &&
            options.DefaultMaxOutputTokens <= options.MaxOutputTokensLimit &&
            options.MaxCorrelationIdLength is > 0 and <= 128;

        return valid
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Model gateway configuration is invalid.");
    }
}
