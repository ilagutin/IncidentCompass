using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Normalization;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.Application.Intake.Redaction;

internal static partial class SecretRedactor
{
    /// <summary>
    /// Written in place of a whole field when a configured pattern exceeded its match timeout. It is
    /// deliberately distinct from <c>[REDACTED]</c> so an operator can tell a value redacted by rule
    /// from a value that defeated the redactor.
    /// </summary>
    public const string PatternTimeoutMarker = "[REDACTED:PATTERN_TIMEOUT]";

    public static NormalizedSignal Redact(NormalizedSignal signal) =>
        Redact(signal, RedactionSettings.Default);

    public static NormalizedSignal Redact(NormalizedSignal signal, RedactionSettings settings) =>
        Redact(signal, settings, canonicalPseudonymVerifier: null);

    public static NormalizedSignal Redact(
        NormalizedSignal signal,
        RedactionSettings settings,
        Func<JsonNode?, string, bool>? canonicalPseudonymVerifier,
        ILogger? logger = null)
    {
        var context = RedactionContext.Create(settings, canonicalPseudonymVerifier, logger, "signal");
        return signal with
        {
            ExternalId = RedactText(signal.ExternalId, context, "externalId"),
            TraceId = RedactText(signal.TraceId, context, "traceId"),
            SpanId = RedactText(signal.SpanId, context, "spanId"),
            ParentSpanId = RedactText(signal.ParentSpanId, context, "parentSpanId"),
            ServiceName = RedactRequiredText(signal.ServiceName, context, "serviceName"),
            Environment = RedactRequiredText(signal.Environment, context, "environment"),
            OperationName = RedactText(signal.OperationName, context, "operationName"),
            Severity = RedactText(signal.Severity, context, "severity"),
            ErrorType = RedactText(signal.ErrorType, context, "errorType"),
            ErrorMessage = RedactText(signal.ErrorMessage, context, "errorMessage"),
            Description = SignalTextTruncator.TruncateDescription(
                RedactText(signal.Description, context, "description")),
            Summary = SignalTextTruncator.TruncateSummary(
                RedactRequiredText(signal.Summary, context, "summary")),
            HttpMethod = RedactText(signal.HttpMethod, context, "httpMethod"),
            HttpRoute = RedactText(signal.HttpRoute, context, "httpRoute"),
            Attributes = RedactNode(signal.Attributes, context.WithPathRoot("attributes"), string.Empty),
            Body = RedactNode(signal.Body, context.WithPathRoot("body"), string.Empty),
        };
    }

    public static string? RedactText(string? text) => RedactText(text, RedactionSettings.Default);

    public static string? RedactText(string? text, RedactionSettings settings) =>
        RedactText(text, RedactionContext.Create(settings, null, null, "text"), string.Empty);

    public static JsonNode RedactJsonNode(JsonNode node) =>
        RedactJsonNode(node, RedactionSettings.Default);

    public static JsonNode RedactJsonNode(JsonNode node, RedactionSettings settings) =>
        RedactNode(node, RedactionContext.Create(settings, null, null, "json"), string.Empty);

    private static string RedactRequiredText(string text, RedactionContext context, string path) =>
        RedactText(text, context, path) ?? string.Empty;

    private static string? RedactText(string? text, RedactionContext context, string path)
    {
        if (text is null)
        {
            return null;
        }

        var redacted = BearerTokenPattern().Replace(text, "Bearer [REDACTED]");
        redacted = AwsAccessKeyPattern().Replace(redacted, "[REDACTED]");
        redacted = SecretPrefixedTokenPattern().Replace(redacted, "[REDACTED]");
        redacted = ConnectionStringPasswordPattern().Replace(redacted, "$1=[REDACTED]");
        foreach (var pattern in context.Patterns.Patterns)
        {
            try
            {
                redacted = pattern.Matcher.Replace(redacted, pattern.Replacement);
            }
            catch (RegexMatchTimeoutException)
            {
                // Fail closed: the partially processed value is discarded rather than returned,
                // because the patterns that had not run yet may be the ones covering this value.
                ReportPatternTimeout(context, pattern, path);
                return PatternTimeoutMarker;
            }
        }

        return redacted;
    }

    private static void ReportPatternTimeout(
        RedactionContext context,
        CompiledRedactionPattern pattern,
        string path)
    {
        if (context.Logger is { } logger && pattern.TryClaimTimeoutReport())
        {
            RedactionTimeoutLog.PatternTimedOut(
                logger,
                pattern.Name,
                context.DescribeField(path),
                PatternTimeoutMarker);
        }
    }

    private static JsonNode RedactNode(JsonNode node, RedactionContext context, string path) =>
        node switch
        {
            JsonObject jsonObject => RedactObject(jsonObject, context, path),
            JsonArray jsonArray => RedactArray(jsonArray, context, path),
            JsonValue jsonValue => RedactValue(jsonValue, context, path),
            _ => node.DeepClone(),
        };

    private static JsonObject RedactObject(JsonObject jsonObject, RedactionContext context, string path)
    {
        var result = new JsonObject();
        foreach (var property in jsonObject)
        {
            var propertyPath = string.IsNullOrEmpty(path) ? property.Key : path + "." + property.Key;
            if (IsSensitiveProperty(property.Key, propertyPath, context.Settings))
            {
                result[property.Key] = context.CanonicalPseudonymVerifier?.Invoke(property.Value, propertyPath) == true
                    ? property.Value!.DeepClone()
                    : "[REDACTED]";
                continue;
            }

            result[property.Key] = property.Value is null
                ? null
                : RedactNode(property.Value, context, propertyPath);
        }

        return result;
    }

    private static JsonArray RedactArray(JsonArray jsonArray, RedactionContext context, string path)
    {
        var result = new JsonArray();
        foreach (var element in jsonArray)
        {
            result.Add(element is null ? null : RedactNode(element, context, path));
        }

        return result;
    }

    private static JsonNode RedactValue(JsonValue jsonValue, RedactionContext context, string path)
    {
        if (jsonValue.GetValueKind() != System.Text.Json.JsonValueKind.String)
        {
            return jsonValue.DeepClone();
        }

        var text = jsonValue.GetValue<string>();
        return JsonValue.Create(RedactText(text, context, path)!)!;
    }

    private static bool IsSensitiveProperty(string key, string path, RedactionSettings settings) =>
        SecretPropertyNameMatcher.IsSensitive(key) ||
        settings.AttributeKeys.Any(candidate =>
            string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-_\.=]{10,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKeyPattern();

    [GeneratedRegex(@"\b(?:(?:sk|glpat|xox[baprs])-[A-Za-z0-9\-_]{10,}|(?:ghp|gho|ghu|ghs)[_-][A-Za-z0-9\-_]{10,}|github_pat_[A-Za-z0-9_]{10,})\b")]
    private static partial Regex SecretPrefixedTokenPattern();

    /// <summary>
    /// A connection-string password runs to the next <c>;</c> and never crosses a line, so the tail
    /// is bounded by line breaks as well. Without that bound the negated class also matches newlines:
    /// on a multi-line value with no later <c>;</c> - a source excerpt containing <c>password ==</c>,
    /// for example - one match would swallow every remaining line and replace the whole excerpt.
    /// Bounding the tail keeps the loss to the one line that actually looks like a credential.
    /// </summary>
    [GeneratedRegex(@"(password|pwd)\s*=\s*[^;\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringPasswordPattern();
}
