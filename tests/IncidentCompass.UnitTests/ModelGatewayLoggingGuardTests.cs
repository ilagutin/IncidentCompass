using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Guards the promise in <c>docs/security-model.md</c> that a rendered prompt and a provider
/// response body never reach a log. That promise is not implemented by a setting that could be
/// turned on; it is implemented by absence. The adapters that hold rendered prompt text and raw
/// provider bodies have no logger, no logger factory and no other output sink, so there is nothing
/// in them that could write one out. This test asserts that absence, so the guarantee survives the
/// next person who wants "just one debug line".
/// <para>
/// Why this assertion and not the narrower ones that were considered:
/// </para>
/// <para>
/// Asserting that the provider client types declare no constructor parameter of a logging type
/// catches only one arrival route. <c>OpenAiModelRequestFactory</c>, <c>OpenAiModelResponseMapper</c>
/// and <c>OpenAiModelErrorMapper</c> are static classes that see the same text and have no
/// constructor at all; a logger reaching them as a method parameter, a static field or an
/// <c>ILoggerFactory</c> would pass such a test untouched. It also cannot see the most common ad-hoc
/// leak of all, a <c>Console.WriteLine</c> left behind while debugging a provider response.
/// </para>
/// <para>
/// Asserting that no log message template in this path names a content-bearing parameter requires
/// deciding which template names mean "content". Placeholder names are free text, so
/// <c>LogDebug("dispatching {Payload}", request)</c> and <c>LogDebug("{TokenCount} tokens", count)</c>
/// are indistinguishable to a matcher; classifying them needs the human judgement the test is
/// supposed to replace, and a structured logger serializes the argument whatever the placeholder is
/// called.
/// </para>
/// <para>
/// Asserting that the types carrying prompt or body text are never passed to a logging call is the
/// rule one actually wants, but it needs to follow a value through locals and fields, which is
/// semantic analysis. A text scan cannot do it, because locals are named freely and
/// <c>logger.LogDebug("{Body}", text)</c> hides what <c>text</c> came from. Loading Roslyn to answer
/// it would still not cover a direct <c>Console</c> or file write.
/// </para>
/// <para>
/// So the rule enforced here is the one the code actually relies on and the one a reviewer can check
/// by eye: this source set reaches no output sink at all. Its cost is that a genuinely safe metric,
/// a latency figure or a token count, cannot be logged from inside these files either. That is the
/// intended answer, not an accident: model-call metadata is logged by
/// <c>InvestigationModelCaller</c> and recorded in the triage ledger, both outside this boundary,
/// where the values being written are backend-derived numbers rather than provider text.
/// </para>
/// <para>
/// What this test is not. It is a text scan over a fixed set of directories, so read it as a
/// tripwire on the obvious routes rather than as a proof:
/// </para>
/// <para>
/// It cannot see indirection. A sink reached through a base class, an interface the guarded type
/// depends on, a wrapper injected as some innocuously named collaborator, an extension method, a
/// delegate handed in at construction, or an <c>HttpMessageHandler</c> registered against the
/// provider client at composition time, is a sink this scan never reads, because the text that names
/// it lives in another file.
/// </para>
/// <para>
/// It cannot see beyond its directory list, which is not the whole of the code that touches prompt
/// text or provider bodies. The prompt is assembled in
/// <c>src/IncidentCompass.Application/Investigation/Jobs</c>, and that folder logs; a raw provider
/// error body is parsed in <c>src/IncidentCompass.Infrastructure/OpenAiCompatible</c>, which is not
/// scanned. Those exclusions are deliberate, because a file that legitimately logs metadata and a
/// file that logs a prompt look identical to a matcher - which is the same reason the scan cannot
/// simply be widened over them. The rule that only metadata is logged there is held by review, and
/// <c>docs/security-model.md</c> says so rather than claiming this test covers it.
/// </para>
/// <para>
/// And it cannot see a marker written so as not to be found: a sink reached by reflection, or a type
/// name assembled from pieces, defeats a substring match by construction. A reviewer looking at a
/// change in these directories should still ask what it does, not just whether this test passed.
/// </para>
/// </summary>
public sealed class ModelGatewayLoggingGuardTests
{
    /// <summary>
    /// The source set that sees a rendered prompt or a raw provider body: the port contracts that
    /// carry the text, the chat-completion adapters and the embedding adapters, including their
    /// provider DTOs and the deterministic mock clients.
    /// </summary>
    private static readonly string[] GuardedDirectories =
    [
        "src/IncidentCompass.Application/Core/Embeddings",
        "src/IncidentCompass.Application/Core/ModelClients",
        "src/IncidentCompass.Infrastructure/Embeddings",
        "src/IncidentCompass.Infrastructure/ModelGateway",
    ];

    /// <summary>
    /// Every way a .NET file can emit text out of the process that a reviewer would recognize on
    /// sight. The list is deliberately about sinks rather than about content: a sink present in this
    /// source set is the failure, regardless of what the author intended to write through it.
    /// <para>
    /// Telemetry counts as a sink here. <c>IRuntimeTelemetry</c> and <c>ActivitySource</c> are
    /// already injected elsewhere in this project, and an activity tag is an exported string like
    /// any log argument, so tagging a span with a prompt inside a guarded adapter has to fail the
    /// same way a <c>Console.WriteLine</c> does. Recording a model call's metadata is what the
    /// accounting path outside this boundary is for.
    /// </para>
    /// <para>
    /// Markers are matched as substrings, so an entry also covers its family: <c>Console.Write</c>
    /// covers <c>Console.WriteLine</c>, <c>Trace.Write</c> covers <c>Trace.WriteLine</c> (but not
    /// the <c>Trace.TraceX</c> methods, which are listed separately), and <c>File.Create</c> covers
    /// <c>File.CreateText</c>. Adding a marker that a guarded file legitimately needs is a signal to
    /// look at that file, not to shorten this list.
    /// </para>
    /// </summary>
    private static readonly string[] OutputSinkMarkers =
    [
        "ActivitySource",
        "Console.Error",
        "Console.Out",
        "Console.Write",
        "Debug.Print",
        "Debug.Write",
        "EventSource",
        "File.AppendAllLines",
        "File.AppendAllText",
        "File.AppendText",
        "File.Create",
        "File.WriteAllBytes",
        "File.WriteAllLines",
        "File.WriteAllText",
        "ILogger",
        "IRuntimeTelemetry",
        "LoggerMessage",
        "Microsoft.Extensions.Logging",
        "Serilog",
        "StreamWriter",
        "Trace.TraceError",
        "Trace.TraceInformation",
        "Trace.TraceWarning",
        "Trace.Write",
        "using static System.Console",
    ];

    [Fact]
    public void ModelGatewayAndEmbeddingSources_ReachNoOutputSink()
    {
        var repositoryRoot = RepositoryRootLocator.Find();
        var failures = new List<string>();

        foreach (var guardedDirectory in GuardedDirectories)
        {
            var directory = Path.Combine(repositoryRoot, guardedDirectory.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory))
            {
                failures.Add(
                    guardedDirectory + " no longer exists. The prompt and response-body text moved" +
                    " somewhere this guard cannot see; point it at the new location rather than" +
                    " deleting the entry.");
                continue;
            }

            var sourcePaths = EnumerateSourceFiles(directory).ToArray();
            if (sourcePaths.Length == 0)
            {
                failures.Add(
                    guardedDirectory + " holds no source files, so this guard would scan nothing." +
                    " Point it at the directory that now holds the provider adapters.");
                continue;
            }

            foreach (var sourcePath in sourcePaths)
            {
                AddOutputSinkFailures(
                    failures,
                    Path.GetRelativePath(repositoryRoot, sourcePath).Replace('\\', '/'),
                    File.ReadAllText(sourcePath),
                    OutputSinkMarkers);
            }
        }

        Assert.Empty(failures.Order(StringComparer.Ordinal).ToArray());
    }

    private static void AddOutputSinkFailures(
        List<string> failures,
        string relativePath,
        string content,
        IReadOnlyCollection<string> markers)
    {
        foreach (var marker in markers.Where(marker => content.Contains(marker, StringComparison.Ordinal)))
        {
            failures.Add(
                relativePath + " references the output sink " + marker + ". This source set holds" +
                " rendered prompt text and raw provider response bodies, and it is kept free of every" +
                " output sink so that none of that text can be written out. There is no setting that" +
                " re-enables prompt logging; see the Logging section of docs/security-model.md." +
                " Record model-call metadata from the investigation model-call path outside this" +
                " boundary instead.");
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
}
