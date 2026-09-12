using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Intake.Fingerprinting;

internal static partial class FingerprintCalculator
{
    public static FingerprintResult Compute(NormalizedSignal signal, int fingerprintVersion)
    {
        return Compute(signal, new EffectiveFingerprintRule(
            FingerprintRuleResolver.DefaultRuleId,
            fingerprintVersion,
            FingerprintInputNames.Default));
    }

    public static FingerprintResult Compute(NormalizedSignal signal, FaultGroupingSettings settings)
    {
        return Compute(signal, FingerprintRuleResolver.Resolve(settings, signal));
    }

    private static FingerprintResult Compute(NormalizedSignal signal, EffectiveFingerprintRule effectiveRule)
    {
        var isStrong = !string.IsNullOrWhiteSpace(signal.ServiceName) &&
            !string.Equals(signal.ServiceName, "unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(signal.ErrorType);

        var signature = string.Join('|', effectiveRule.Inputs.Select(input => Normalize(ValueFor(input, signal))));
        var value = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));

        return new FingerprintResult(value, isStrong ? FingerprintStrength.Strong : FingerprintStrength.Weak, effectiveRule);
    }

    private static string ValueFor(string input, NormalizedSignal signal) => input switch
    {
        FingerprintInputNames.ServiceName => signal.ServiceName,
        FingerprintInputNames.Environment => signal.Environment,
        FingerprintInputNames.ErrorType => signal.ErrorType ?? string.Empty,
        FingerprintInputNames.ErrorMessage => Mask(signal.ErrorMessage ?? string.Empty),
        FingerprintInputNames.RouteOrOperation => Mask(signal.HttpRoute ?? signal.OperationName ?? string.Empty),
        FingerprintInputNames.OperationName => Mask(signal.OperationName ?? string.Empty),
        FingerprintInputNames.HttpRoute => Mask(signal.HttpRoute ?? string.Empty),
        FingerprintInputNames.Severity => signal.Severity ?? string.Empty,
        FingerprintInputNames.Source => signal.Source,
        _ => throw new ArgumentOutOfRangeException(nameof(input), input, "Unsupported fingerprint input.")
    };

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    /// <summary>
    /// Replaces the parts of a free-text input that change between two occurrences of the same fault:
    /// timestamps, GUIDs, email addresses, long hexadecimal runs and numbers. This is what keeps a
    /// fingerprint stable across runs, and it also means a caller cannot make a masked input unique by
    /// appending a hexadecimal identifier to it. Service name and error type are never masked.
    /// </summary>
    private static string Mask(string value)
    {
        var masked = TimestampPattern().Replace(value, "<ts>");
        masked = GuidPattern().Replace(masked, "<guid>");
        masked = EmailPattern().Replace(masked, "<email>");
        masked = LongHexPattern().Replace(masked, "<hex>");
        masked = NumberPattern().Replace(masked, "<num>");
        return masked;
    }

    [GeneratedRegex(@"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?\b")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8,}\b")]
    private static partial Regex LongHexPattern();

    [GeneratedRegex(@"\b\d+")]
    private static partial Regex NumberPattern();
}
