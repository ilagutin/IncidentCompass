using System.Security.Cryptography;
using System.Text;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The adapter binding fingerprint a frozen <c>code_write</c> approval is tied to: which host
/// directories the configured workspace resolves to, stated as a digest.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for.</b> The approval contract records this value on the action row and compares it
/// again before dispatch, so an approval taken against one wiring cannot execute against another. A
/// logical target alone cannot carry that: <c>source:configured-workspace</c> stays the same string
/// when an operator repoints a monitored root at a different checkout, and the approved diff would
/// then be applied to a tree nobody approved. Including the resolved roots means such an edit turns
/// the approval into <c>adapter_binding_changed</c> instead.
/// </para>
/// <para>
/// <b>Why hashing a path here is safe.</b> Host paths must not be logged or persisted, and none is:
/// the paths are inputs to a SHA-256 digest, that digest is an input to the binding hash, and only
/// the binding hash leaves this type. Nothing derives a path from either.
/// </para>
/// <para>
/// <b>The authority is a constant, not an endpoint.</b> There is no remote. The workspace is the
/// local filesystem, so the field the other adapters fill with a provider authority is filled with a
/// fixed scheme that says exactly that, and the variable part of the binding is entirely in the
/// resolved roots.
/// </para>
/// </remarks>
internal static class RemediationWorkspaceBinding
{
    private const string ToolKind = "source_workspace";
    private const string LocalAuthority = "file://local-workspace";

    /// <summary>
    /// ASCII record separator. A path may hold any printable character, so separating the fields
    /// with one that cannot appear in a path is what stops two different root lists from flattening
    /// to the same string.
    /// </summary>
    private const char FieldSeparator = (char)0x1e;

    public static string ComputeFingerprint(SourceContextOptions options) =>
        ExternalActionBinding.ComputeFingerprint(
            ToolKind,
            RemediationApplyToolDescriptor.LogicalTargetId,
            LocalAuthority,
            ComputeResourceId(options));

    private static string ComputeResourceId(SourceContextOptions options)
    {
        var builder = new StringBuilder(options.WorkspaceRoot ?? string.Empty);
        foreach (var root in (options.Roots ?? [])
                     .OrderBy(static root => root.ServiceName, StringComparer.Ordinal)
                     .ThenBy(static root => root.Release, StringComparer.Ordinal)
                     .ThenBy(static root => root.RootPath, StringComparer.Ordinal))
        {
            builder.Append(FieldSeparator).Append(root.ServiceName)
                .Append(FieldSeparator).Append(root.Release)
                .Append(FieldSeparator).Append(root.RootPath);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
