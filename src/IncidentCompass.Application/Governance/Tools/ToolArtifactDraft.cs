using System.Text.Json.Nodes;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Governance.Tools;

/// <summary>
/// What a worker tool hands back when it wants a durable artifact: the kind, the domain reference and
/// the payload it assembled, exactly as the connector produced it.
/// <para>
/// It is deliberately not a <see cref="TriageArtifact"/>. A tool cannot build one, so a tool cannot
/// persist a payload that skipped redaction: the only conversion is
/// <see cref="RedactedToolArtifactFactory"/>, which redacts the payload, canonicalizes it and hashes
/// the redacted form. A future tool that forgets to redact does not compile.
/// </para>
/// <para>
/// <see cref="Id"/> is assigned here rather than by the factory because a tool names the artifact in
/// its own model-visible output before the artifact exists. Identity is not the safety property;
/// redaction is, and the factory still owns that.
/// </para>
/// <para>
/// <see cref="DomainRef"/> is an <see cref="ArtifactDomainRef"/> rather than a <c>string</c> for the
/// same reason the payload is a draft rather than an artifact. A domain reference is assembled out
/// of connector text too, and a bare string beside a redacted payload was a way for a tool to put an
/// arbitrary connector value into a stored column without meeting anything. The type bounds its
/// shape at construction and the factory redacts it on the way out; see
/// <see cref="ArtifactDomainRef"/> for what each half covers and what neither does.
/// </para>
/// </summary>
/// <remarks>
/// This is a class rather than a record on purpose: a record's synthesized <c>ToString</c> prints
/// every member, which would put not-yet-redacted connector text into any log line that formats a
/// draft. See the logging rules in <c>docs/security-model.md</c>.
/// </remarks>
public sealed class ToolArtifactDraft(ArtifactKind kind, ArtifactDomainRef domainRef, JsonNode payload)
{
    public Guid Id { get; } = Guid.NewGuid();

    public ArtifactKind Kind { get; } = kind;

    public ArtifactDomainRef DomainRef { get; } = domainRef;

    public JsonNode Payload { get; } = payload;
}
