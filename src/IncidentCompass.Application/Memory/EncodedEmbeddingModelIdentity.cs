namespace IncidentCompass.Application.Memory;

/// <summary>
/// The model name a locally embedded memory corpus is identified by:
/// <c>&lt;model id&gt;@sha256:&lt;first 16 lowercase hex of the model file's SHA-256&gt;</c>.
/// <para>
/// A corpus records it as its embedding model, so a model file replaced under the same id is a
/// different vector space to <see cref="MemoryCorpusIdentity.Matches" /> rather than a change nobody
/// can see. The host that has the model composes it from the installed manifest; a host without the
/// model can still read the id part.
/// </para>
/// </summary>
internal static class EncodedEmbeddingModelIdentity
{
    public const string DigestMarker = "@sha256:";

    public const int DigestPrefixLength = 16;

    public static string Encode(string modelId, string modelFileSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        if (modelId.Contains(DigestMarker, StringComparison.Ordinal))
        {
            throw new ArgumentException("A model id cannot contain the digest marker.", nameof(modelId));
        }

        if (modelFileSha256 is not { Length: 64 } || !IsLowercaseHex(modelFileSha256))
        {
            throw new ArgumentException(
                "The model file digest must be a SHA-256 written as 64 lowercase hexadecimal characters.",
                nameof(modelFileSha256));
        }

        return modelId + DigestMarker + modelFileSha256[..DigestPrefixLength];
    }

    /// <summary>
    /// The id part of an embedding model name: everything before the digest marker, or the whole name
    /// when it carries none.
    /// </summary>
    public static string ModelIdOf(string embeddingModel)
    {
        ArgumentNullException.ThrowIfNull(embeddingModel);
        var marker = embeddingModel.IndexOf(DigestMarker, StringComparison.Ordinal);
        return marker < 0 ? embeddingModel : embeddingModel[..marker];
    }

    public static bool TryParse(string? embeddingModel, out string modelId, out string digestPrefix)
    {
        modelId = string.Empty;
        digestPrefix = string.Empty;
        if (embeddingModel is null)
        {
            return false;
        }

        var marker = embeddingModel.IndexOf(DigestMarker, StringComparison.Ordinal);
        if (marker <= 0)
        {
            return false;
        }

        var digest = embeddingModel[(marker + DigestMarker.Length)..];
        if (digest.Length != DigestPrefixLength || !IsLowercaseHex(digest))
        {
            return false;
        }

        modelId = embeddingModel[..marker];
        digestPrefix = digest;
        return true;
    }

    private static bool IsLowercaseHex(string value) =>
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
