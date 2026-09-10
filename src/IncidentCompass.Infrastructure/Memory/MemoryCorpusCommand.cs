using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The operator entry point for the memory corpus: <c>memory status</c> reports what the corpus is
/// relative to the configured embedding route, and <c>memory rebuild</c> re-embeds every reviewed
/// seed file under that route and publishes the result as a new generation.
/// </summary>
/// <remarks>
/// <para>
/// It is a console command on the existing hosts rather than an HTTP endpoint, for the same reason
/// the triage configuration validator is: this is a host-wide maintenance action with no
/// tenant-scoped caller behind it, and adding a write API for it would need an administrative
/// identity this system does not have. It also stays a read-only surface for memory as far as the
/// API is concerned, which is the point of a corpus assembled from reviewed files.
/// </para>
/// <para>
/// Both verbs run before the host starts, so the seed hosted service is not competing with them
/// inside this process. Another process is a different matter, and the reconciliation transaction
/// is what settles that: it takes the same owner-scoped advisory lock the startup pass takes, so a
/// rebuild and a startup synchronization publish one after the other rather than interleaving.
/// </para>
/// </remarks>
public static class MemoryCorpusCommand
{
    private const string Verb = "memory";
    private const string StatusAction = "status";
    private const string RebuildAction = "rebuild";

    public static async Task<int?> RunIfRequestedAsync(
        string[] args,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);
        if (args.Length != 2 || !string.Equals(args[0], Verb, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rebuild = string.Equals(args[1], RebuildAction, StringComparison.OrdinalIgnoreCase);
        if (!rebuild && !string.Equals(args[1], StatusAction, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            await using var scope = services.CreateAsyncScope();
            RequireEnabledSeeding(scope.ServiceProvider);
            if (rebuild)
            {
                await RebuildAsync(scope.ServiceProvider, cancellationToken);
            }

            return Report(await ReadStatusAsync(scope.ServiceProvider, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The reconciliation transaction never committed, so the previous corpus is still the
            // current one. Saying so is the whole message: an operator who interrupts a rebuild
            // needs to know the corpus was not left half-replaced.
            Console.Error.WriteLine(
                "Memory corpus rebuild was cancelled before publication; the previous corpus is unchanged.");
            return 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            Console.Error.WriteLine("Memory corpus command failed: " + exception.Message);
            return 1;
        }
    }

    private static async Task RebuildAsync(IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        var synchronizer = scopedServices.GetRequiredService<MemorySeedSynchronizer>();
        var outcome = await synchronizer.SynchronizeAsync(MemorySeedSyncMode.Rebuild, cancellationToken);
        Console.WriteLine(
            "Rebuilt memory corpus generation " + outcome.Generation?.ToString() + " from " +
            outcome.ItemCount.ToString(CultureInfo.InvariantCulture) + " reviewed files.");
    }

    private static Task<MemoryCorpusSnapshot> ReadStatusAsync(
        IServiceProvider scopedServices,
        CancellationToken cancellationToken) =>
        scopedServices.GetRequiredService<IMemoryCorpusStatusReader>().GetAsync(cancellationToken);

    private static void RequireEnabledSeeding(IServiceProvider scopedServices)
    {
        if (!scopedServices.GetRequiredService<IOptions<MemorySeedOptions>>().Value.Enabled)
        {
            throw new InvalidOperationException(
                "Memory seeding is disabled, so this host has no configured corpus scope or source" +
                " directory to act on. Set IncidentCompass:Memory:Seed:Enabled to true and rerun.");
        }
    }

    private static int Report(MemoryCorpusSnapshot snapshot)
    {
        Console.WriteLine(
            "Memory corpus " + snapshot.TenantId + "/" + snapshot.Owner + ": " + snapshot.State);
        Console.WriteLine(
            "  configured route: " + snapshot.ConfiguredRouteId + " provider=" +
            snapshot.ConfiguredProviderId + " model=" + snapshot.ConfiguredModel);
        Console.WriteLine(
            "  active corpus: provider=" + (snapshot.ActiveProviderId ?? "unrecorded") +
            " adapter=" + (snapshot.ActiveEmbeddingProvider ?? "none") +
            " model=" + (snapshot.ActiveEmbeddingModel ?? "none") +
            " dimensions=" + Format(snapshot.ActiveEmbeddingDimensions));
        Console.WriteLine(
            "  counts: items=" + snapshot.ActiveItemCount.ToString(CultureInfo.InvariantCulture) +
            " chunks=" + snapshot.ActiveChunkCount.ToString(CultureInfo.InvariantCulture) +
            " embeddingIdentities=" +
            snapshot.ActiveEmbeddingIdentityCount.ToString(CultureInfo.InvariantCulture));
        if (!snapshot.RebuildRequired)
        {
            return 0;
        }

        Console.Error.WriteLine(
            "The active corpus was not built under the configured embedding route, so memory_search" +
            " finds nothing in it. It is intact and still retrievable under the route that built it." +
            " Run 'memory rebuild' to re-embed every reviewed file under the configured route.");
        return 1;
    }

    private static string Format(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "none";
}
