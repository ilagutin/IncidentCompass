using System.Net;

namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// Downloads one missing artifact file. The body is written to a temporary file beside the
/// destination and hashed while it streams; only a file whose SHA-256 is the pinned one is renamed
/// into place, and a mismatch or any failure removes the temporary file and renames nothing.
/// <para>
/// Hugging Face answers a resolve URL with a redirect to its content delivery network. Redirects are
/// followed by hand, at most <see cref="MaxRedirects" /> of them, and only to https locations. The
/// integrity of the file comes from its digest, not from where it was served; refusing plaintext
/// hops keeps the download itself private and unmodified in transit.
/// </para>
/// <para>
/// Every download is bounded in size. A response that declares more than the limit is refused
/// before anything is written, and a body that runs past the limit without declaring it is cut off
/// and discarded, so a misbehaving origin cannot fill the model volume before the digest check.
/// </para>
/// </summary>
internal sealed class LocalOnnxModelFileFetcher(HttpClient httpClient)
{
    public const int MaxRedirects = 5;

    private const int BufferSize = 81920;

    public async Task FetchAsync(
        LocalOnnxModelArtifact artifact,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var temporaryPath = LocalOnnxModelLayout.CreateTemporaryDownloadPath(destinationPath);
        try
        {
            var actualSha256 = await DownloadAsync(artifact, temporaryPath, maxBytes, cancellationToken);
            if (!string.Equals(actualSha256, artifact.Sha256, StringComparison.Ordinal))
            {
                throw new LocalOnnxModelStoreException(
                    LocalOnnxModelErrorCodes.DigestMismatch,
                    $"The downloaded {artifact.Kind} file {artifact.Path} has SHA-256 {actualSha256}, not the" +
                    $" pinned {artifact.Sha256}; it was discarded.");
            }

            if (!TryMoveIntoPlace(temporaryPath, destinationPath))
            {
                LocalOnnxModelFiles.TryDeleteTemporaryFile(temporaryPath);
                await VerifyConcurrentlyPlacedFileAsync(artifact, destinationPath, cancellationToken);
            }
        }
        catch (LocalOnnxModelStoreException)
        {
            LocalOnnxModelFiles.TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LocalOnnxModelFiles.TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                              or UnauthorizedAccessException or OperationCanceledException)
        {
            LocalOnnxModelFiles.TryDeleteTemporaryFile(temporaryPath);
            throw FetchFailed(artifact, exception.GetType().Name, exception);
        }
    }

    /// <summary>
    /// Renames the verified download into place, never over an existing file. Returns false when the
    /// destination appeared while this download ran, which is another installer finishing first.
    /// </summary>
    private static bool TryMoveIntoPlace(string temporaryPath, string destinationPath)
    {
        try
        {
            File.Move(temporaryPath, destinationPath, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            return false;
        }
    }

    /// <summary>
    /// A file another installer placed first is accepted when its digest is the pinned one, which is
    /// all this store asks of any file, and refused with the digest code otherwise. It is left in
    /// place either way.
    /// </summary>
    private static async Task VerifyConcurrentlyPlacedFileAsync(
        LocalOnnxModelArtifact artifact,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var existingSha256 = await LocalOnnxModelFiles.ComputeSha256Async(destinationPath, cancellationToken);
        if (!string.Equals(existingSha256, artifact.Sha256, StringComparison.Ordinal))
        {
            throw new LocalOnnxModelStoreException(
                LocalOnnxModelErrorCodes.DigestMismatch,
                $"The {artifact.Kind} file {artifact.Path} appeared while it was being downloaded and has" +
                $" SHA-256 {existingSha256}, not {artifact.Sha256}; it was left in place and not replaced.");
        }
    }

    private async Task<string> DownloadAsync(
        LocalOnnxModelArtifact artifact,
        string temporaryPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var response = await SendFollowingHttpsRedirectsAsync(artifact, cancellationToken);
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > maxBytes)
        {
            throw TooLarge(artifact, maxBytes, $"the response declares {declaredLength} bytes");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous);
        var digest = await LocalOnnxModelFiles.CopyAndHashAsync(body, file, maxBytes, cancellationToken)
            ?? throw TooLarge(artifact, maxBytes, "the response body ran past it");
        await file.FlushAsync(cancellationToken);
        file.Flush(flushToDisk: true);
        return digest;
    }

    private async Task<HttpResponseMessage> SendFollowingHttpsRedirectsAsync(
        LocalOnnxModelArtifact artifact,
        CancellationToken cancellationToken)
    {
        var location = new Uri(artifact.Url);
        for (var redirects = 0; ; redirects++)
        {
            // Only the host is ever named: a content delivery location carries a signed query string.
            if (!string.Equals(location.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw FetchFailed(artifact, $"a {location.Scheme} location on host {location.Host} was refused");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, location);
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var statusCode = (int)response.StatusCode;
            var next = IsRedirect(response.StatusCode) ? response.Headers.Location : null;
            response.Dispose();
            if (next is null)
            {
                throw FetchFailed(artifact, $"host {location.Host} answered HTTP {statusCode}");
            }

            if (redirects == MaxRedirects)
            {
                throw FetchFailed(artifact, $"more than {MaxRedirects} redirects");
            }

            location = next.IsAbsoluteUri ? next : new Uri(location, next);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static LocalOnnxModelStoreException FetchFailed(
        LocalOnnxModelArtifact artifact,
        string reason,
        Exception? innerException = null) =>
        new(
            LocalOnnxModelErrorCodes.FetchFailed,
            $"The {artifact.Kind} file {artifact.Path} could not be downloaded: {reason}.",
            innerException);

    private static LocalOnnxModelStoreException TooLarge(LocalOnnxModelArtifact artifact, long maxBytes, string reason) =>
        new(
            LocalOnnxModelErrorCodes.DownloadTooLarge,
            $"The {artifact.Kind} file {artifact.Path} was not downloaded: {reason}, over the {maxBytes}-byte" +
            " download limit.");
}
