using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;

namespace IncidentCompass.Application.Core.ModelGateway;

public sealed class AiModelException : ProviderException
{
    public AiModelException(
        string provider,
        string message,
        string? errorCode = null,
        string? providerErrorCode = null,
        Exception? innerException = null,
        ProviderFailureKind failureKind = ProviderFailureKind.Unknown,
        AiModelUsage? usage = null,
        string? returnedModel = null)
        : base(provider, message, errorCode, providerErrorCode, innerException, failureKind)
    {
        Usage = usage;
        ReturnedModel = returnedModel;
    }

    public AiModelUsage? Usage { get; }

    public string? ReturnedModel { get; }
}
