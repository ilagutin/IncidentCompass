namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The outcome of one materialization: either a workspace the caller now owns and must dispose, or a
/// refusal code and nothing to clean up.
/// </summary>
internal sealed record SourceWorkspaceResult(SourceWorkspace? Workspace, string Code)
{
    public static SourceWorkspaceResult Created(SourceWorkspace workspace) =>
        new(workspace, SourceWorkspaceCodes.Created);

    public static SourceWorkspaceResult Refused(string code) => new(null, code);

    public bool IsCreated => Workspace is not null;
}
