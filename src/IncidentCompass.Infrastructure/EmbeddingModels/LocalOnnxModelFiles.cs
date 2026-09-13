using System.Buffers;
using System.Security.Cryptography;

namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// File primitives of the model store: digests computed while streaming, and best-effort removal of
/// a temporary file the store itself created.
/// </summary>
internal static class LocalOnnxModelFiles
{
    private const int BufferSize = 81920;

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Copies <paramref name="source" /> to <paramref name="destination" /> and returns the SHA-256 of
    /// exactly the bytes written, so a download is hashed in one pass and never read back. Returns
    /// <see langword="null" />, having stopped copying, as soon as the source delivers more than
    /// <paramref name="maxBytes" />; the bytes past the limit are never written.
    /// </summary>
    public static async Task<string?> CopyAndHashAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var total = 0L;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    return null;
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Removes a temporary file this store created. Failure to remove it is ignored: the name is
    /// unique to one attempt, nothing ever reads a temporary name, and the failure being handled is
    /// the one worth reporting.
    /// </summary>
    public static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
