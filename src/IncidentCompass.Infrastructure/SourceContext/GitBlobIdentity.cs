using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The name git gives a file's bytes: the lower-hex SHA-1 of <c>"blob " + length + "\0" + bytes</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists beside <see cref="SourceTreeIdentity" />.</b> That type names a whole tree the
/// way this product names one, with SHA-256 over the admitted set. A remote repository names one file
/// the way git does, and the two hash families have nothing to say to each other. Proving that an
/// approved local base and a remote commit hold the same bytes at the same path needs one value both
/// sides can compute, and this is it. It is the only bridge, and it is arithmetic: no git binary, no
/// process, no <c>.git</c> directory, no network.
/// </para>
/// <para>
/// <b>SHA-1 is a naming function here, not a security one.</b> Nothing is authorized because two blob
/// ids match. A correspondence built on these ids is one half of a check whose other half is the
/// SHA-256 tree identity the approval was taken against, and the identity is what a mismatch refuses
/// on. The algorithm is fixed by git's object format and is not a choice this type gets to make.
/// </para>
/// </remarks>
internal static class GitBlobIdentity
{
    /// <summary>Characters in a lower-hex SHA-1.</summary>
    public const int IdentityCharacters = 40;

    public static string Compute(ReadOnlySpan<byte> content)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + content.Length.ToString(CultureInfo.InvariantCulture) + "\0");
#pragma warning disable CA5350 // Git object names are SHA-1 by format; nothing is authorized by one.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5350
        hash.AppendData(header);
        hash.AppendData(content);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static async Task<string> ComputeAsync(
        Stream content,
        long length,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
#pragma warning disable CA5350 // Git object names are SHA-1 by format; nothing is authorized by one.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5350
        hash.AppendData(Encoding.ASCII.GetBytes(
            "blob " + length.ToString(CultureInfo.InvariantCulture) + "\0"));
        var read = 0L;
        int chunk;
        while ((chunk = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            read += chunk;
            if (read > length)
            {
                // The declared length is part of the name, so a file that grew under the read would
                // otherwise be given the name of bytes it no longer holds.
                throw new IOException("File grew while its git blob identity was being computed.");
            }

            hash.AppendData(buffer.AsSpan(0, chunk));
        }

        return read == length
            ? Convert.ToHexStringLower(hash.GetHashAndReset())
            : throw new IOException("File shrank while its git blob identity was being computed.");
    }

    public static bool IsValid(string? value) =>
        value is { Length: IdentityCharacters } &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
