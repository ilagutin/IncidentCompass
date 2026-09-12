using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.SourceContext;

internal sealed class SourceLookupTool(ISourceContextLookup sourceContextLookup) : IImmediateAgentTool
{
    public AiToolDefinition Definition { get; } = new(
        "source_lookup",
        "Inspect bounded source context for backend-selected stack frames in the current release.",
        "v1",
        CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject()
        }));

    public ToolValidationResult Validate(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any())
        {
            return ToolValidationResult.Invalid(
                "invalid_arguments",
                "source_lookup accepts only an empty object.");
        }

        return ToolValidationResult.Valid(CanonicalJsonSerializer.ToElement(new JsonObject()));
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        AgentToolExecutionContext context,
        JsonElement sanitizedArguments,
        CancellationToken cancellationToken)
    {
        if (!context.Configuration.CurrentReleases.TryGetValue(context.FaultServiceName, out var release) ||
            string.IsNullOrWhiteSpace(release))
        {
            return SuccessfulOutcome(SourceLookupResult.Unavailable("source_release_unavailable"), release: null);
        }

        if (context.TriggerSignal is null)
        {
            return SuccessfulOutcome(SourceLookupResult.Unavailable("source_signal_unavailable"), release);
        }

        var frames = SourceStackTraceExtractor.Extract(context.TriggerSignal);
        if (frames.Count == 0)
        {
            return SuccessfulOutcome(SourceLookupResult.NoMatch("source_frames_not_found"), release);
        }

        var result = await sourceContextLookup.LookupAsync(
            new SourceLookupRequest(context.FaultServiceName, release, frames),
            cancellationToken);
        return SuccessfulOutcome(result, release);
    }

    /// <summary>
    /// The limitation a match is reported through when its release and repository-relative path
    /// cannot be expressed as an <see cref="ArtifactDomainRef"/> - a path past the segment cap, which
    /// a deep checkout produces on its own, or a release name a colon or an invisible character got
    /// into. The match is dropped rather than stored under a reference that does not describe it,
    /// and the read boundary's own limitation list is where a dropped match already gets said, so
    /// this joins the codes that list already carries instead of inventing a second channel.
    /// <para>
    /// It is emitted at most once per call however many matches were dropped. The code says a
    /// reference could not be built, which is one fact whether it happened once or three times, and
    /// the model reads this list as a set of reasons rather than as a tally. The read boundary's own
    /// codes are per frame and do repeat, which is why the dedupe is applied to this code alone
    /// rather than to the assembled list.
    /// </para>
    /// </summary>
    private const string UnrepresentableReferenceCode = "source_reference_rejected";

    private static ToolExecutionResult SuccessfulOutcome(
        SourceLookupResult result,
        string? release)
    {
        // One pathological path costs one match, not the investigation: the tool still succeeds with
        // the matches that are representable, and the caller learns a match was withheld from the
        // limitation rather than from an exception that nothing on the worker path classifies.
        var matched = new List<SourceLookupMatch>(result.Matches.Count);
        var drafts = new List<ToolArtifactDraft>(result.Matches.Count);
        var limitations = result.Limitations.ToList();
        var droppedAMatch = false;
        foreach (var match in result.Matches)
        {
            if (CreateDraft(match) is not { } draft)
            {
                droppedAMatch = true;
                continue;
            }

            matched.Add(match);
            drafts.Add(draft);
        }

        if (droppedAMatch)
        {
            limitations.Add(new SourceLookupLimitation(UnrepresentableReferenceCode));
        }

        return new ToolExecutionResult(
            ToolExecutionStatus.Succeeded,
            CreateOutput(result, matched, drafts, limitations, release),
            Artifacts: drafts);
    }

    private static ToolArtifactDraft? CreateDraft(SourceLookupMatch match)
    {
        if (ArtifactDomainRef.TryCreate("source", match.Release, match.RelativePath) is not { } reference)
        {
            return null;
        }

        var payload = new JsonObject
        {
            ["evidenceKind"] = "SourceCode",
            ["relativePath"] = match.RelativePath,
            ["lineStart"] = match.LineStart,
            ["lineEnd"] = match.LineEnd,
            ["excerpt"] = match.Excerpt,
            ["release"] = match.Release,
            ["mappingMethod"] = match.MappingMethod
        };
        return new ToolArtifactDraft(ArtifactKind.RetrievedItem, reference, payload);
    }

    private static JsonElement CreateOutput(
        SourceLookupResult result,
        List<SourceLookupMatch> matches,
        List<ToolArtifactDraft> drafts,
        List<SourceLookupLimitation> limitations,
        string? release)
    {
        var items = new JsonArray();
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            items.Add(new JsonObject
            {
                ["artifactId"] = drafts[index].Id.ToString(),
                ["title"] = match.RelativePath,
                ["relativePath"] = match.RelativePath,
                ["lineStart"] = match.LineStart,
                ["lineEnd"] = match.LineEnd,
                ["quote"] = match.Excerpt,
                ["release"] = match.Release,
                ["mappingMethod"] = match.MappingMethod
            });
        }

        var outcome = result.Outcome switch
        {
            SourceLookupOutcome.Matched => "matched",
            SourceLookupOutcome.NoMatch => "no_match",
            _ => "connector_unavailable"
        };
        var message = result.Outcome switch
        {
            SourceLookupOutcome.Matched => "matches found",
            SourceLookupOutcome.NoMatch => "no matches",
            _ => "connector unavailable"
        };
        // When every match was dropped for an unrepresentable reference, `matched` is false while
        // `outcome` stays "matched". The two are not contradicting each other: `outcome` reports what
        // the read boundary found, and it did find files, while `matched` reports whether anything
        // survived that a report could cite. Collapsing `outcome` to "no_match" would tell the model
        // the release holds no such source, which is a different and false statement. The
        // source_reference_rejected code in `limitations` is what says why the gap is there.
        return CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["matched"] = result.Outcome == SourceLookupOutcome.Matched && items.Count > 0,
            ["message"] = message,
            ["outcome"] = outcome,
            ["code"] = result.Code,
            ["release"] = release,
            ["items"] = items,
            ["limitations"] = new JsonArray(limitations.Select(item => JsonValue.Create(item.Code)).ToArray()),
            ["noMatchReason"] = result.Outcome == SourceLookupOutcome.Matched ? null : result.Code
        });
    }
}
