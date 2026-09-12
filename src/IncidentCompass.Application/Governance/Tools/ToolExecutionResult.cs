using System.Text.Json;
using IncidentCompass.Domain.Governance;

namespace IncidentCompass.Application.Governance.Tools;

/// <summary>
/// What one immediate worker tool produced. <see cref="Output"/> and <see cref="Artifacts"/> are both
/// unredacted connector text: the caller redacts the output and turns each draft into a durable
/// artifact through <see cref="RedactedToolArtifactFactory"/>. A tool cannot return a persisted
/// artifact shape, which is what keeps the redaction step from being optional.
/// </summary>
public sealed record ToolExecutionResult(
    ToolExecutionStatus Status,
    JsonElement Output,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    IReadOnlyCollection<ToolArtifactDraft>? Artifacts = null);
