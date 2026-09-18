using System.Text.Json.Nodes;

namespace IncidentCompass.Application.Investigation.Jobs;

internal static class TriageClassificationVocabulary
{
    public const string Unknown = "Unknown";

    /// <summary>
    /// The one classification a ticket and a remediation diff follow from, and therefore the one
    /// <c>MemoryCitationConfirmationRule</c> is scoped to. Named here so the rule and the vocabulary
    /// cannot drift apart.
    /// </summary>
    public const string KnownIncident = "KnownIncident";

    private static readonly IReadOnlyList<string> Values =
    [
        KnownIncident,
        "LikelyRegression",
        "SimpleKnownError",
        Unknown,
        "Noise"
    ];

    private static readonly HashSet<string> ValueSet = new(Values, StringComparer.Ordinal);

    public static bool Contains(string value)
    {
        return ValueSet.Contains(value);
    }

    public static JsonArray ToJsonArray()
    {
        var array = new JsonArray();
        foreach (var value in Values)
        {
            array.Add(value);
        }

        return array;
    }
}
