using System.Text;
using IncidentCompass.Application.Core.Text;

namespace IncidentCompass.Application.Investigation.Jobs;

internal sealed class WorkerOutputValidationException : InvalidOperationException
{
    internal const string TruncationMarker =
        "Additional output violations were omitted after the reporting limit.";

    public WorkerOutputValidationException(
        IReadOnlyList<string> violations,
        bool violationsTruncated)
        : base(BuildMessage(violations, violationsTruncated))
    {
        Violations = violations.ToArray();
        ViolationsTruncated = violationsTruncated;
    }

    public IReadOnlyList<string> Violations { get; }

    public bool ViolationsTruncated { get; }

    public string GetSafeDiagnostic(int maxLength) => TextTruncator.Truncate(Message, maxLength);

    private static string BuildMessage(IReadOnlyList<string> violations, bool violationsTruncated)
    {
        var builder = new StringBuilder("Worker output validation failed:");
        foreach (var violation in violations)
        {
            builder.Append(' ').Append(violation);
        }

        if (violationsTruncated)
        {
            builder.Append(' ').Append(TruncationMarker);
        }

        return builder.ToString();
    }
}
