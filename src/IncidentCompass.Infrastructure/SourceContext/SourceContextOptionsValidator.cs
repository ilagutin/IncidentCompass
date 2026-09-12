using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.SourceContext;

internal sealed class SourceContextOptionsValidator : IValidateOptions<SourceContextOptions>
{
    public ValidateOptionsResult Validate(string? name, SourceContextOptions options)
    {
        var failures = new List<string>();
        RequireRange(options.MaxFrames, 1, 8, nameof(options.MaxFrames), failures);
        RequireRange(options.MaxCandidateFiles, 1, 1000, nameof(options.MaxCandidateFiles), failures);
        RequireRange(options.MaxSourceBytes, 1024, 1024 * 1024, nameof(options.MaxSourceBytes), failures);
        RequireRange(options.MaxExcerptLines, 1, 100, nameof(options.MaxExcerptLines), failures);
        ValidateExtensions(options.AllowedExtensions, failures);
        ValidateRoots(options.Roots, failures);
        ValidateWorkspaceRoot(options.WorkspaceRoot, failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateExtensions(string[]? extensions, List<string> failures)
    {
        if (extensions is null || extensions.Length == 0)
        {
            failures.Add("AllowedExtensions must contain at least one extension.");
            return;
        }

        foreach (var extension in extensions)
        {
            if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith('.') ||
                extension.IndexOfAny(['/', '\\']) >= 0)
            {
                failures.Add("AllowedExtensions entries must be dot-prefixed file extensions.");
            }
        }
    }

    /// <summary>
    /// An unset workspace root means remediation is off, which is the shipped state and not a
    /// misconfiguration. A set one must be absolute, for the same reason a monitored root must: a
    /// relative path resolves against whatever the host's working directory happens to be, and the
    /// directory copies are written into is not a thing to leave to that.
    /// </summary>
    private static void ValidateWorkspaceRoot(string? workspaceRoot, List<string> failures)
    {
        if (workspaceRoot is not null && !IsAbsolutePath(workspaceRoot))
        {
            failures.Add("WorkspaceRoot must be absolute when it is set.");
        }
    }

    private static void ValidateRoots(IReadOnlyCollection<SourceContextRootOptions>? roots, List<string> failures)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots ?? [])
        {
            if (string.IsNullOrWhiteSpace(root.ServiceName) || root.ServiceName.Length > 128 ||
                string.IsNullOrWhiteSpace(root.Release) || root.Release.Length > 128)
            {
                failures.Add("Each source root requires bounded nonblank ServiceName and Release values.");
            }

            if (!IsAbsolutePath(root.RootPath))
            {
                failures.Add("Each source RootPath must be absolute.");
            }

            if (!keys.Add(root.ServiceName + "\n" + root.Release))
            {
                failures.Add("Source root ServiceName and Release mappings must be unique.");
            }

            if ((root.BuildPathPrefixes ?? []).Any(prefix => !IsAbsolutePath(prefix)))
            {
                failures.Add("BuildPathPrefixes entries must be absolute paths.");
            }
        }
    }

    internal static bool IsAbsolutePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Path.IsPathRooted(value) ||
            (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '/' or '\\');
    }

    private static void RequireRange(int value, int minimum, int maximum, string name, List<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add($"{name} must be between {minimum} and {maximum}.");
        }
    }
}
