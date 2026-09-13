using IncidentCompass.Application.Core.ModelGateway;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Configuration;

/// <summary>
/// Refuses a model gateway provider the host cannot serve while the host is starting. That includes
/// <see cref="ProviderKind.LocalOnnx" />, which parses as a provider kind because the embedding
/// gateway shares the parser, but has no chat adapter; refusing it here is what keeps the model
/// gateway's selector arm for that kind unreachable.
/// </summary>
internal sealed class ModelGatewayProviderOptionsValidator : IValidateOptions<ModelGatewayOptions>
{
    public ValidateOptionsResult Validate(string? name, ModelGatewayOptions options)
    {
        if (!ProviderKindParser.TryParse(options.Provider, out var kind))
        {
            return ValidateOptionsResult.Fail(
                $"Model gateway provider '{options.Provider}' is unsupported.");
        }

        return kind == ProviderKind.LocalOnnx
            ? ValidateOptionsResult.Fail(
                $"Model gateway provider '{options.Provider}' is unsupported: LocalOnnx is an" +
                " embedding-only provider with no chat adapter, and is valid only for" +
                " IncidentCompass:Embeddings:Provider.")
            : ValidateOptionsResult.Success;
    }
}
