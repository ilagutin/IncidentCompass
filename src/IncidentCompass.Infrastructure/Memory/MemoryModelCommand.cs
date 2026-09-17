using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The operator entry point for the local embedding model. <c>memory model status</c> reports the
/// installed model, the model the memory route is configured to use and the model the active corpus
/// was built with, and exits 1 when any of them disagree. <c>memory model install</c> installs the
/// configured model beside the installed one, switches to it and says how to roll back.
/// </summary>
/// <remarks>
/// Like <see cref="MemoryCorpusCommand" />, it runs on the Worker before the host starts, so it reads
/// and writes the model store directly rather than consulting the start-time install pass.
/// </remarks>
public static class MemoryModelCommand
{
    private const string Verb = "memory";
    private const string Noun = "model";
    private const string StatusAction = "status";
    private const string InstallAction = "install";

    public static async Task<int?> RunIfRequestedAsync(
        string[] args,
        IServiceProvider services,
        TextWriter? output = null,
        TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);
        if (args.Length != 3 ||
            !string.Equals(args[0], Verb, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(args[1], Noun, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var install = string.Equals(args[2], InstallAction, StringComparison.OrdinalIgnoreCase);
        if (!install && !string.Equals(args[2], StatusAction, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        output ??= Console.Out;
        error ??= Console.Error;
        try
        {
            await using var scope = services.CreateAsyncScope();
            var scopedServices = scope.ServiceProvider;
            var pin = RequireLocalModelOptions(scopedServices).CreatePin();
            var store = scopedServices.GetRequiredService<LocalOnnxModelStore>();
            return install
                ? await InstallAsync(store, pin, output, error, cancellationToken)
                : await ReportStatusAsync(scopedServices, store, pin, output, error, cancellationToken);
        }
        catch (LocalOnnxModelStoreException exception)
        {
            error.WriteLine("Local embedding model command failed with " + exception.ErrorCode + ": " + exception.Message);
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("Local embedding model command was cancelled before the active manifest was replaced.");
            return 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            error.WriteLine("Local embedding model command failed: " + exception.Message);
            return 1;
        }
    }

    /// <summary>
    /// Bounded by <see cref="LocalOnnxEmbeddingOptions.InstallTimeoutSeconds" />, exactly as the Worker's
    /// start-time install pass is, so every install on the volume ends within the same bound. That bound
    /// is what lets an installer remove another installer's abandoned downloads safely.
    /// </summary>
    private static async Task<int> InstallAsync(
        LocalOnnxModelStore store,
        LocalOnnxModelPin pin,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(pin.InstallTimeoutSeconds));
        using var installCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        LocalOnnxModelInstallResult result;
        try
        {
            result = await store.InstallConfiguredAsync(pin, installCancellation.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            error.WriteLine(
                "Local embedding model command failed with " + LocalOnnxModelErrorCodes.InstallTimedOut +
                ": the install did not complete within " + pin.InstallTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " seconds. The active manifest was not replaced.");
            return 1;
        }

        var manifest = result.Model.Manifest;
        if (!result.ManifestSwitched)
        {
            WriteModel(output, "Local embedding model already active", manifest);
            output.WriteLine("  Both files were verified; nothing was changed.");
            return 0;
        }

        WriteModel(output, "Installed local embedding model", manifest);
        var manifestPath = LocalOnnxModelLayout.GetManifestPath(Path.GetFullPath(pin.ModelDirectory!));
        if (result.PreviousManifestPath is null)
        {
            output.WriteLine("  No model was active before, so there is no previous manifest to roll back to.");
        }
        else
        {
            output.WriteLine("  The previously active manifest is kept at " + result.PreviousManifestPath + ".");
        }

        output.WriteLine(
            "  Next: make sure the memory embedding route names model " + manifest.Id +
            ", run 'memory rebuild' to re-embed the corpus with it, and restart the Worker. A running Worker" +
            " keeps the model it verified at start until it is restarted.");
        if (result.PreviousManifestPath is not null)
        {
            output.WriteLine(
                "  Roll back: copy " + result.PreviousManifestPath + " over " + manifestPath +
                " and restart the Worker, which keeps the model it verified at start until it is restarted." +
                " The old model files are still in place, and the previous corpus generation stays current" +
                " until 'memory rebuild' publishes a new one.");
        }

        return 0;
    }

    private static async Task<int> ReportStatusAsync(
        IServiceProvider scopedServices,
        LocalOnnxModelStore store,
        LocalOnnxModelPin pin,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var installed = await store.ReadInstalledAsync(pin, cancellationToken);
        var configuration = await scopedServices.GetRequiredService<ITriageConfigurationRepository>()
            .GetCurrentAsync(cancellationToken);
        var route = MemoryEmbeddingRouteResolver.Resolve(configuration);
        var providerKind = configuration.Providers.TryGetValue(route.ProviderId, out var provider)
            ? provider.Kind
            : "unknown";
        var corpusModel = await ReadActiveCorpusModelAsync(scopedServices, cancellationToken);

        if (installed is null)
        {
            output.WriteLine("Local embedding model: not installed");
        }
        else
        {
            WriteModel(output, "Local embedding model", installed.Manifest);
        }

        output.WriteLine(
            "Configured route: " + route.RouteId + " provider=" + route.ProviderId + " kind=" + providerKind +
            " model=" + route.Model);
        output.WriteLine("Active corpus model: " + (corpusModel ?? "none"));

        var problems = FindProblems(installed, route, corpusModel);
        foreach (var problem in problems)
        {
            error.WriteLine(problem);
        }

        return problems.Count == 0 ? 0 : 1;
    }

    private static List<string> FindProblems(
        LocalOnnxInstalledModel? installed,
        MemoryEmbeddingRoute route,
        string? corpusModel)
    {
        if (installed is null)
        {
            return ["No local embedding model is installed. Run 'memory model install'."];
        }

        var problems = new List<string>();
        var manifest = installed.Manifest;
        if (!string.Equals(manifest.Id, route.Model, StringComparison.Ordinal))
        {
            problems.Add(
                "The installed model " + manifest.Id + " is not the model route " + route.RouteId + " names (" +
                route.Model + "). Install the configured model or correct the route.");
        }

        var identity = LocalOnnxModelIdentity.Describe(manifest);
        if (!string.Equals(corpusModel, identity, StringComparison.Ordinal))
        {
            problems.Add(
                "The active corpus was built with " + (corpusModel ?? "no model") + ", not with the installed" +
                " model " + identity + ". Run 'memory rebuild' once the installed model is the configured one.");
        }

        return problems;
    }

    private static async Task<string?> ReadActiveCorpusModelAsync(
        IServiceProvider scopedServices,
        CancellationToken cancellationToken)
    {
        var settings = scopedServices.GetRequiredService<IOptions<MemorySeedOptions>>().Value;
        var inventory = await scopedServices.GetRequiredService<IMemoryRepository>()
            .GetCorpusInventoryAsync(settings.TenantId, settings.Owner, cancellationToken);
        return inventory.ActiveIdentities.Count == 1
            ? inventory.ActiveIdentities[0].EmbeddingModel
            : inventory.Current?.Identity.EmbeddingModel;
    }

    private static LocalOnnxEmbeddingOptions RequireLocalModelOptions(IServiceProvider scopedServices)
    {
        var provider = scopedServices.GetRequiredService<IOptions<EmbeddingOptions>>().Value.Provider;
        if (!ProviderKindParser.IsLocalOnnx(provider))
        {
            throw new InvalidOperationException(
                "The host embedding provider is '" + provider + "'. Set IncidentCompass:Embeddings:Provider to" +
                " LocalOnnx to manage a local embedding model.");
        }

        var options = scopedServices.GetRequiredService<IOptions<LocalOnnxEmbeddingOptions>>().Value;
        var failures = LocalOnnxEmbeddingOptionsValidator.FindFailures(options);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", failures));
        }

        return options;
    }

    private static void WriteModel(TextWriter output, string heading, LocalOnnxModelManifest manifest)
    {
        output.WriteLine(heading + ": " + manifest.Id);
        output.WriteLine("  revision=" + manifest.Revision + " license=" + manifest.License);
        output.WriteLine("  identity=" + LocalOnnxModelIdentity.Describe(manifest));
        output.WriteLine("  model file sha256=" + manifest.ModelFile.Sha256);
        output.WriteLine("  tokenizer file sha256=" + manifest.TokenizerFile.Sha256);
    }
}
