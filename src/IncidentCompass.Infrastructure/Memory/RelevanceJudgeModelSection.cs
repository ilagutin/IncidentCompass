using System.Globalization;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Relevance;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The relevance judge's half of <c>memory model status</c> and <c>memory model install</c>. It is a
/// collaborator of <see cref="MemoryModelCommand" /> rather than a verb of its own: one directory per
/// model is an implementation detail of the store, and an operator manages the models this Worker
/// runs with one command.
/// </summary>
/// <remarks>
/// Every failure here is reported and turned into an exit code; none of them escapes. A judge that is
/// not configured, not installed or not the configured one is a reported problem, so a broken judge
/// never hides the embedding model's report, and the command still exits non-zero.
/// </remarks>
internal static class RelevanceJudgeModelSection
{
    /// <summary>What the command prints for a Worker that runs the mock relevance judge.</summary>
    public const string MockJudgeStatusLine =
        "Relevance judge: Mock (deterministic stand-in, not a governance boundary)";

    public static async Task<int> RunAsync(
        IServiceProvider scopedServices,
        bool install,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryReadProvider(scopedServices, output, error, out var isMock))
            {
                return 1;
            }

            if (isMock)
            {
                return ReportMockJudge(install, output, error);
            }

            var options = ReadConfiguredOptions(scopedServices, output, error);
            if (options is null)
            {
                return 1;
            }

            var store = scopedServices.GetRequiredService<LocalOnnxModelStore>();
            return install
                ? await InstallAsync(store, options, output, error, cancellationToken)
                : await ReportStatusAsync(store, options, output, error, cancellationToken);
        }
        catch (LocalOnnxModelStoreException exception)
        {
            error.WriteLine(
                "Local relevance judge command failed with " +
                LocalOnnxRelevanceJudgeStoreErrorCodeMap.Map(exception.ErrorCode) + ": " + exception.Message);
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("Local relevance judge command was cancelled before the active manifest was replaced.");
            return 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            error.WriteLine("Local relevance judge command failed: " + exception.Message);
            return 1;
        }
    }

    /// <summary>
    /// Which judge this Worker is configured to run. A host that never composed the provider setting
    /// runs the local judge, which is its default. An invalid provider is reported here, because this
    /// command runs before the host starts and start-up validation has not happened yet.
    /// </summary>
    private static bool TryReadProvider(
        IServiceProvider scopedServices,
        TextWriter output,
        TextWriter error,
        out bool isMock)
    {
        isMock = false;
        var accessor = scopedServices.GetService<IOptions<RelevanceJudgeOptions>>();
        if (accessor is null)
        {
            return true;
        }

        try
        {
            isMock = RelevanceJudgeOptionsValidator.IsMock(accessor.Value.Provider);
            return true;
        }
        catch (OptionsValidationException exception)
        {
            output.WriteLine("Relevance judge: provider not valid");
            error.WriteLine("The relevance judge provider is not valid on this host. " + string.Join(" ", exception.Failures));
            return false;
        }
    }

    /// <summary>
    /// A Worker that runs the mock judge is reported as exactly that, never as a local judge that is
    /// not configured, which would be false while a judge is running and confirming matches.
    /// <para>
    /// It exits 1, for status and install alike. The mock is the right judge for the mock stack and
    /// the wrong one anywhere else, and the command cannot tell which host it is on, so it answers the
    /// question an operator's script actually asks of it: does this Worker run the shipped judge,
    /// installed and verified? A mock does not, and a script that waits for exit 0 before trusting a
    /// host, or before starting an evaluation, must not be satisfied by one. Nothing on the mock stack
    /// depends on 0: its embedding half already exits 1, because the mock embedding provider has no
    /// installed model either.
    /// </para>
    /// </summary>
    private static int ReportMockJudge(bool install, TextWriter output, TextWriter error)
    {
        output.WriteLine(MockJudgeStatusLine);
        error.WriteLine(
            (install
                ? "There is no relevance judge model to install: this Worker runs the mock judge, which needs none. "
                : "This Worker runs the mock relevance judge, which has no model to verify. ") +
            "It confirms memory matches by matching error-type names, is correct only on the mock stack and" +
            " must never run on a production host. Set " + RelevanceJudgeOptions.ProviderKey + " to " +
            RelevanceJudgeOptions.LocalOnnxProvider + " to run the shipped judge.");
        return 1;
    }

    /// <summary>
    /// The configured judge settings, or <see langword="null" /> once the reason there are none has
    /// been reported.
    /// <para>
    /// Reading <c>IOptions.Value</c> is where the host's own validator runs, and it throws
    /// <see cref="OptionsValidationException" />, which is not an
    /// <see cref="InvalidOperationException" /> and would otherwise escape this section entirely.
    /// Nothing else would have reported it either: this command runs before the host starts, so
    /// start-up validation has not happened yet. Both ways of having no usable judge settings, a
    /// blank directory and a directory with something else wrong beside it, are therefore reported
    /// the same way here.
    /// </para>
    /// </summary>
    private static LocalOnnxRelevanceJudgeOptions? ReadConfiguredOptions(
        IServiceProvider scopedServices,
        TextWriter output,
        TextWriter error)
    {
        IReadOnlyList<string> failures;
        LocalOnnxRelevanceJudgeOptions? options = null;
        try
        {
            options = scopedServices.GetRequiredService<IOptions<LocalOnnxRelevanceJudgeOptions>>().Value;
            failures = LocalOnnxRelevanceJudgeOptionsValidator.FindFailures(options);
        }
        catch (OptionsValidationException exception)
        {
            failures = [.. exception.Failures];
        }

        if (failures.Count == 0)
        {
            return options;
        }

        output.WriteLine("Local relevance judge: not configured");
        error.WriteLine("The local relevance judge is not configured on this host. " + string.Join(" ", failures));
        return null;
    }

    /// <summary>
    /// Bounded by <see cref="LocalOnnxRelevanceJudgeOptions.InstallTimeoutSeconds" />, exactly as the
    /// Worker's start-time judge install pass is, so every install on the volume ends within the same
    /// bound. That bound is what lets an installer remove another installer's abandoned downloads.
    /// </summary>
    private static async Task<int> InstallAsync(
        LocalOnnxModelStore store,
        LocalOnnxRelevanceJudgeOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.InstallTimeoutSeconds));
        using var installCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        LocalOnnxModelInstallResult result;
        try
        {
            result = await store.InstallConfiguredAsync(options.CreatePin(), installCancellation.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            error.WriteLine(
                "Local relevance judge command failed with " +
                LocalOnnxRelevanceJudgeProvider.InstallTimedOutErrorCode +
                ": the install did not complete within " +
                options.InstallTimeoutSeconds.ToString(CultureInfo.InvariantCulture) +
                " seconds. The active judge manifest was not replaced.");
            return 1;
        }

        var manifest = result.Model.Manifest;
        if (!result.ManifestSwitched)
        {
            WriteJudge(output, "Local relevance judge already active", manifest);
            output.WriteLine("  Both judge files were verified; nothing was changed.");
            return 0;
        }

        WriteJudge(output, "Installed local relevance judge", manifest);
        var manifestPath = LocalOnnxModelLayout.GetManifestPath(Path.GetFullPath(options.ModelDirectory!));
        if (result.PreviousManifestPath is null)
        {
            output.WriteLine("  No judge was active before, so there is no previous judge manifest to roll back to.");
        }
        else
        {
            output.WriteLine("  The previously active judge manifest is kept at " + result.PreviousManifestPath + ".");
        }

        output.WriteLine(
            "  Next: restart the Worker so it verifies the installed judge. A running Worker keeps the" +
            " judge it verified at start until it is restarted. The corpus is not affected: a judge" +
            " reranks what a search already retrieved, and nothing it returns is stored.");
        if (result.PreviousManifestPath is not null)
        {
            output.WriteLine(
                "  Roll back the judge: copy " + result.PreviousManifestPath + " over " + manifestPath +
                " and restart the Worker. The old judge files are still in place.");
        }

        return 0;
    }

    private static async Task<int> ReportStatusAsync(
        LocalOnnxModelStore store,
        LocalOnnxRelevanceJudgeOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var installed = await store.ReadInstalledAsync(options.CreatePin(), cancellationToken);
        if (installed is null)
        {
            output.WriteLine("Local relevance judge: not installed");
            error.WriteLine("No local relevance judge is installed. Run 'memory model install'.");
            return 1;
        }

        WriteJudge(output, "Local relevance judge", installed.Manifest);
        var problems = FindProblems(installed.Manifest, options);
        foreach (var problem in problems)
        {
            error.WriteLine(problem);
        }

        return problems.Count == 0 ? 0 : 1;
    }

    private static List<string> FindProblems(LocalOnnxModelManifest manifest, LocalOnnxRelevanceJudgeOptions options)
    {
        var problems = new List<string>();
        if (!string.Equals(manifest.Kind, LocalOnnxModelManifest.RelevanceJudgeKind, StringComparison.Ordinal))
        {
            problems.Add(
                "The model installed in the judge directory, " + manifest.Id + ", is of kind " + manifest.Kind +
                ", not " + LocalOnnxModelManifest.RelevanceJudgeKind + ". Point" +
                " IncidentCompass:RelevanceJudge:LocalOnnx:ModelDirectory at a directory of its own.");
        }
        else if (!string.Equals(manifest.Id, options.ModelId, StringComparison.Ordinal))
        {
            problems.Add(
                "The installed judge " + manifest.Id + " is not the configured judge (" + options.ModelId +
                "). Install the configured judge or correct the configuration.");
        }

        return problems;
    }

    private static void WriteJudge(TextWriter output, string heading, LocalOnnxModelManifest manifest)
    {
        output.WriteLine(heading + ": " + manifest.Id);
        output.WriteLine("  judge revision=" + manifest.Revision + " license=" + manifest.License);
        output.WriteLine("  judge model file sha256=" + manifest.ModelFile.Sha256);
        output.WriteLine("  judge tokenizer file sha256=" + manifest.TokenizerFile.Sha256);
    }
}
