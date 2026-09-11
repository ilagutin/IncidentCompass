using System.Text.Json.Nodes;
using IncidentCompass.Application.Remediation;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The closed argument shape a pull-request proposal is stated in.
/// </summary>
/// <remarks>
/// It is declared with <c>additionalProperties: false</c> and an exhaustive required list, so a
/// proposal carrying one extra field is refused rather than partly honoured. There is no title, body,
/// repository, remote, credential, reviewer, label, draft or merge field in it: the published text is
/// composed by the backend from these values and is not a thing a caller states, and the head branch is
/// constrained to the one pattern the backend derives.
/// </remarks>
internal static class PullRequestArgumentSchema
{
    public static JsonObject Build() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["properties"] = new JsonObject
        {
            ["baseBranch"] = Text(),
            ["baseCommitSha"] = Hex(RemediationPayloadFields.GitObjectNameCharacters),
            ["baseTreeIdentity"] = Hex(64),
            ["correspondenceDigest"] = Hex(64),
            ["excludedPathCount"] = Count(),
            ["filesChanged"] = Count(),
            ["headBranch"] = new JsonObject
            {
                ["type"] = "string",
                ["pattern"] = "^incidentcompass/remediation/[0-9a-f]{32}$"
            },
            ["headCommitSha"] = Hex(RemediationPayloadFields.GitObjectNameCharacters),
            ["issueNumber"] = Count(),
            ["originReportId"] = Identifier(),
            ["predecessorActionId"] = Identifier(),
            ["predecessorResultSha256"] = Hex(64),
            ["provedPathCount"] = Count(),
            ["release"] = Text(),
            ["reportConfidence"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("Low", "Medium", "High")
            },
            ["repository"] = Text(),
            ["resultTreeIdentity"] = Hex(64),
            ["serviceName"] = Text()
        },
        ["required"] = new JsonArray(
            "baseBranch", "baseCommitSha", "baseTreeIdentity", "correspondenceDigest",
            "excludedPathCount", "filesChanged", "headBranch", "headCommitSha", "issueNumber",
            "originReportId", "predecessorActionId", "predecessorResultSha256", "provedPathCount",
            "release", "reportConfidence", "repository", "resultTreeIdentity", "serviceName")
    };

    private static JsonObject Text() => new() { ["type"] = "string" };

    private static JsonObject Count() => new() { ["type"] = "integer" };

    private static JsonObject Identifier() => new()
    {
        ["type"] = "string",
        ["pattern"] = "^[0-9a-f]{32}$"
    };

    private static JsonObject Hex(int characters) => new()
    {
        ["type"] = "string",
        ["pattern"] = $"^[0-9a-f]{{{characters}}}$"
    };
}
