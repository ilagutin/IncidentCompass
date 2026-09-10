using System.Globalization;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Fingerprinting;
using static IncidentCompass.Infrastructure.Intake.TriageConfigurationValidationGuards;

namespace IncidentCompass.Infrastructure.Intake;

internal static class FaultGroupingSettingsLoadValidator
{
    private static readonly HashSet<string> SupportedSources = new(StringComparer.Ordinal)
    {
        "otel", "user", "tester", "manual"
    };

    public static void Validate(FaultGroupingSettings settings)
    {
        if (settings.LookbackMinutes <= 0)
        {
            throw Invalid("FaultGrouping.LookbackMinutes", settings.LookbackMinutes.ToString(CultureInfo.InvariantCulture), "a positive integer");
        }

        if (settings.SilenceWindowMinutes <= 0)
        {
            throw Invalid("FaultGrouping.SilenceWindowMinutes", settings.SilenceWindowMinutes.ToString(CultureInfo.InvariantCulture), "a positive integer");
        }

        if (settings.FingerprintVersion <= 0)
        {
            throw Invalid("FaultGrouping.FingerprintVersion", settings.FingerprintVersion.ToString(CultureInfo.InvariantCulture), "a positive integer");
        }

        ValidateFingerprintRules(settings.Rules);
        ValidateSuppressionRules(settings.SuppressionPolicies);

        if (settings.Recurrence is { EscalateAfterCount: <= 0 } recurrence)
        {
            throw Invalid("FaultGrouping.Recurrence.EscalateAfterCount", recurrence.EscalateAfterCount.ToString(CultureInfo.InvariantCulture), "a positive integer");
        }

        if (settings.MassIssue.MinNeighborCount <= 0)
        {
            throw Invalid("FaultGrouping.MassIssue.MinNeighborCount", settings.MassIssue.MinNeighborCount.ToString(CultureInfo.InvariantCulture), "a positive integer");
        }

        if (!settings.MassIssue.TryGetMinimumFingerprintStrength(out _))
        {
            throw Invalid("FaultGrouping.MassIssue.MinFingerprintStrength", settings.MassIssue.MinFingerprintStrength, "one of: weak, strong");
        }
    }

    private static void ValidateFingerprintRules(IReadOnlyCollection<FingerprintRuleSettings> rules)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules.ElementAt(index);
            var prefix = $"FaultGrouping.FingerprintRules[{index}]";
            RequireNonBlank(prefix + ".Id", rule.Id);
            if (!ids.Add(rule.Id))
            {
                throw Invalid(prefix + ".Id", rule.Id, "a unique rule id");
            }

            if (rule.Version <= 0)
            {
                throw Invalid(prefix + ".Version", rule.Version.ToString(CultureInfo.InvariantCulture), "a positive integer");
            }

            ValidateSelector(prefix + ".ServiceName", rule.ServiceName);
            ValidateSelector(prefix + ".OperationName", rule.OperationName);
            if (!string.IsNullOrWhiteSpace(rule.Source) && !SupportedSources.Contains(rule.Source))
            {
                throw Invalid(prefix + ".Source", rule.Source, "one of: otel, user, tester, manual");
            }

            if (rule.Inputs is null || rule.Inputs.Count == 0)
            {
                throw Invalid(prefix + ".Inputs", string.Empty, "one or more supported fingerprint inputs");
            }

            var inputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var input in rule.Inputs)
            {
                RequireKnown(prefix + ".Inputs", input, FingerprintInputNames.Supported);
                if (!inputs.Add(input))
                {
                    throw Invalid(prefix + ".Inputs", input, "unique fingerprint inputs");
                }
            }
        }
    }


    private static void ValidateSuppressionRules(IReadOnlyCollection<SuppressionRuleSettings> rules)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules.ElementAt(index);
            var prefix = $"FaultGrouping.SuppressionRules[{index}]";
            RequireNonBlank(prefix + ".Id", rule.Id);
            if (!ids.Add(rule.Id))
            {
                throw Invalid(prefix + ".Id", rule.Id, "a unique rule id");
            }

            if (rule.SilenceWindowMinutes <= 0)
            {
                throw Invalid(prefix + ".SilenceWindowMinutes", rule.SilenceWindowMinutes.ToString(CultureInfo.InvariantCulture), "a positive integer");
            }

            ValidateSelector(prefix + ".ServiceName", rule.ServiceName);
            ValidateSelector(prefix + ".Severity", rule.Severity);
        }
    }

    private static void ValidateSelector(string settingName, string? value)
    {
        if (value is not null)
        {
            RequireNonBlank(settingName, value);
        }
    }
}
