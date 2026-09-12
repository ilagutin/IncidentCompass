using System.Text.Json.Nodes;
using IncidentCompass.Application.Remediation;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The closed argument shape a branch push proposal is stated in.
/// </summary>
/// <remarks>
/// It is declared with <c>additionalProperties: false</c> and an exhaustive required list, so a
/// proposal carrying one extra field is refused rather than partly honoured. There is no repository,
/// remote, credential or force field in it, and the branch name is constrained to the one pattern the
/// backend derives, so the schema itself says what a caller may not choose.
/// </remarks>
internal static class BranchPushArgumentSchema
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
            ["baseTreeSha"] = Hex(RemediationPayloadFields.GitObjectNameCharacters),
            ["branchName"] = new JsonObject
            {
                ["type"] = "string",
                ["pattern"] = "^incidentcompass/remediation/[0-9a-f]{32}$"
            },
            ["commitTimestamp"] = new JsonObject
            {
                ["type"] = "string",
                ["pattern"] = "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$"
            },
            ["correspondenceDigest"] = Hex(64),
            ["excludedPathCount"] = Count(),
            ["filesChanged"] = Count(),
            ["originReportId"] = Identifier(),
            ["patch"] = Text(),
            ["patchBytes"] = Count(),
            ["predecessorActionId"] = Identifier(),
            ["predecessorResultSha256"] = Hex(64),
            ["provedPathCount"] = Count(),
            ["release"] = Text(),
            ["repository"] = Text(),
            ["resultTreeIdentity"] = Hex(64),
            ["serviceName"] = Text()
        },
        ["required"] = new JsonArray(
            "baseBranch", "baseCommitSha", "baseTreeIdentity", "baseTreeSha", "branchName",
            "commitTimestamp", "correspondenceDigest", "excludedPathCount", "filesChanged",
            "originReportId", "patch", "patchBytes", "predecessorActionId",
            "predecessorResultSha256", "provedPathCount", "release", "repository",
            "resultTreeIdentity", "serviceName")
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
