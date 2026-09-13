namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// A model store failure with its stable code from <see cref="LocalOnnxModelErrorCodes" />. It never
/// crosses the port: the install pass records the code, and the adapter reports it as an
/// <c>EmbeddingClientException</c>.
/// </summary>
internal sealed class LocalOnnxModelStoreException : Exception
{
    public LocalOnnxModelStoreException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
