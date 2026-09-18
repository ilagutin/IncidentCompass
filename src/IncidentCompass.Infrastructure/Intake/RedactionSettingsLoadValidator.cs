using System.Text.RegularExpressions;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using static IncidentCompass.Infrastructure.Intake.TriageConfigurationValidationGuards;

namespace IncidentCompass.Infrastructure.Intake;

internal static class RedactionSettingsLoadValidator
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Property names an operator may not turn into a redaction attribute key, because redacting them
    /// would not hide a secret but silently weaken a governance decision.
    /// <para>
    /// <c>retrievalConfidence</c> is the band the publication rule reads. Redaction replaces a
    /// matching property's value with the redaction marker whatever its kind, so naming this key
    /// would turn every confirmed match into a value that confirms nothing, on the artifact payload
    /// as well as in the tool result: every <c>KnownIncident</c> resting on memory would then be
    /// refused, and the operator would have configured a system that cannot reach its own strongest
    /// classification. It is refused at load for the same reason a role output schema that types a
    /// secret-named property as a non-string is refused: the configuration defeats itself.
    /// </para>
    /// <para>
    /// <c>confirmationScore</c> is the relevance judge's score the band was decided from. Redacting it
    /// would leave every judged band on the artifact and in the tool result without the number that
    /// explains it, so a reviewer could no longer tell a band the judge confirmed from one it did not.
    /// It is reserved with the band for that reason.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ReservedAttributeKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            MemoryRetrievalConfidence.PayloadPropertyName,
            MemoryRetrievalConfidence.ConfirmationScorePropertyName
        };

    public static void Validate(RedactionSettings settings)
    {
        ValidateKeys(settings.AttributeKeys, "Redaction.AttributeKeys");
        ValidateKeys(settings.UserIdentifierAttributes, "Redaction.UserIdentifierAttributes");

        for (var index = 0; index < settings.Patterns.Count; index++)
        {
            var pattern = settings.Patterns.ElementAt(index);
            var section = $"Redaction.Patterns[{index}]";
            RequireNonBlank(section + ".Name", pattern.Name);
            RequireNonBlank(section + ".Pattern", pattern.Pattern);
            RequireNonBlank(section + ".Replacement", pattern.Replacement);
            try
            {
                _ = new Regex(pattern.Pattern, RegexOptions.None, PatternTimeout);
            }
            catch (ArgumentException exception)
            {
                throw Invalid(section + ".Pattern", pattern.Pattern, "a valid .NET regular expression: " + exception.Message);
            }
        }
    }

    private static void ValidateKeys(IReadOnlyCollection<string> keys, string section)
    {
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            RequireNonBlank(section, key);
            if (!distinct.Add(key))
            {
                throw Invalid(section, key, "unique attribute keys");
            }

            if (ReservedAttributeKeys.Contains(key))
            {
                throw Invalid(
                    section,
                    key,
                    "an attribute key that is not " + MemoryRetrievalConfidence.PayloadPropertyName +
                        " or " + MemoryRetrievalConfidence.ConfirmationScorePropertyName +
                        "; redacting the retrieval band or the score it was decided from would make every" +
                        " confirmed memory match stop confirming or stop explaining why it does");
            }
        }
    }
}
