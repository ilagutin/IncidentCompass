using System.Text.Json.Nodes;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Intake.Redaction;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void RedactText_RedactsBearerTokenButKeepsSurroundingText()
    {
        const string input = "Auth failed calling upstream with Bearer abcdef1234567890ghijklmnop, retrying";

        var result = SecretRedactor.RedactText(input)!;

        Assert.Contains("Auth failed calling upstream with Bearer [REDACTED], retrying", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef1234567890ghijklmnop", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactText_RedactsAwsAccessKey()
    {
        const string input = "leaked key AKIAABCDEFGHIJKLMNOP in log line";

        var result = SecretRedactor.RedactText(input)!;

        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAABCDEFGHIJKLMNOP", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactText_NullInput_ReturnsNullWithoutThrowing()
    {
        var result = SecretRedactor.RedactText(null);

        Assert.Null(result);
    }

    [Fact]
    public void RedactText_TextWithNoSecrets_IsReturnedUnchanged()
    {
        const string input = "Checkout timed out after 30000ms calling payments-api";

        var result = SecretRedactor.RedactText(input)!;

        Assert.Equal(input, result);
    }

    [Fact]
    public void Redact_PreservesNullOptionalTextFields()
    {
        var signal = new NormalizedSignal(
            Source: "tester",
            ExternalId: null,
            TraceId: null,
            SpanId: null,
            ParentSpanId: null,
            ServiceName: "payments-api",
            Environment: "prod",
            OperationName: null,
            Severity: null,
            ErrorType: null,
            ErrorMessage: null,
            Summary: "Checkout failed",
            Description: null,
            HttpMethod: null,
            HttpRoute: null,
            HttpStatusCode: null,
            DurationMs: null,
            Attributes: new JsonObject(),
            Body: new JsonObject(),
            ObservedAtUtc: DateTimeOffset.UtcNow);

        var redacted = SecretRedactor.Redact(signal);

        Assert.Null(redacted.ErrorMessage);
        Assert.Null(redacted.Description);
        Assert.Equal("Checkout failed", redacted.Summary);
    }

    /// <summary>
    /// A connection-string password ends at the next <c>;</c> or at the end of its line, whichever
    /// comes first. The line bound is what keeps a multi-line value, such as a source excerpt with a
    /// <c>Password ==</c> comparison and no later <c>;</c>, from losing every remaining line to one
    /// match.
    /// </summary>
    [Fact]
    public void RedactText_ConnectionStringPasswordStopsAtTheEndOfItsLine()
    {
        var input = string.Join(
            '\n',
            "if (request.Password == expectedHash)",
            "{",
            "    Retry(attempt)",
            "}");

        var result = SecretRedactor.RedactText(input)!;

        Assert.Equal(4, result.Split('\n').Length);
        Assert.DoesNotContain("expectedHash", result, StringComparison.Ordinal);
        Assert.Contains("    Retry(attempt)", result, StringComparison.Ordinal);
        Assert.Equal(
            "Host=db;Password=[REDACTED];Database=checkout",
            SecretRedactor.RedactText("Host=db;Password=hunter2;Database=checkout"));
    }

    /// <summary>
    /// The other half of the same bound, pinned so it cannot change silently. A credential whose value
    /// continues onto the next line - backslash continuation, as `.env`, `.properties`, shell scripts
    /// and Dockerfiles use it - is no longer redacted past the line break. The name and the first line
    /// go, the continued tail stays. That is a knowing loss, taken because the unbounded rule destroyed
    /// a whole source excerpt every time it fired on ordinary code; `docs/trade-offs.md` records it.
    /// </summary>
    [Fact]
    public void RedactText_LeavesACredentialTailThatContinuesPastTheLineBreak()
    {
        var result = SecretRedactor.RedactText("DB_PASSWORD=hunter2\\\nsupersecret-tail")!;

        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
        Assert.StartsWith("DB_PASSWORD=[REDACTED]", result, StringComparison.Ordinal);
        Assert.Contains("supersecret-tail", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactJsonNode_RedactsPasswordKeyAtNestedDepth()
    {
        var node = JsonNode.Parse("""
            {
                "level1": {
                    "level2": {
                        "password": "hunter2",
                        "keep": "value"
                    }
                }
            }
            """)!;

        var redacted = SecretRedactor.RedactJsonNode(node);

        Assert.Equal("[REDACTED]", redacted["level1"]!["level2"]!["password"]!.GetValue<string>());
        Assert.Equal("value", redacted["level1"]!["level2"]!["keep"]!.GetValue<string>());
    }

    [Fact]
    public void RedactJsonNode_RedactsSecretInsideArrayElement()
    {
        var node = JsonNode.Parse("""
            { "lines": ["normal line", "Bearer abcdef1234567890ghijklmnop leaked here"] }
            """)!;

        var redacted = SecretRedactor.RedactJsonNode(node);

        var lines = redacted["lines"]!.AsArray();
        Assert.Equal("normal line", lines[0]!.GetValue<string>());
        Assert.Contains("Bearer [REDACTED]", lines[1]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void RedactJsonNode_NoSecrets_ContentIsUnchanged()
    {
        var node = JsonNode.Parse("""{"serviceName":"payments-api","count":3}""")!;

        var redacted = SecretRedactor.RedactJsonNode(node);

        Assert.Equal("payments-api", redacted["serviceName"]!.GetValue<string>());
        Assert.Equal(3, redacted["count"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("ghp_1234567890abcdefghijklmnopqrstuvwxyzAB")]
    [InlineData("gho_1234567890abcdefghijklmnopqrstuvwxyzAB")]
    [InlineData("ghu_1234567890abcdefghijklmnopqrstuvwxyzAB")]
    [InlineData("ghs_1234567890abcdefghijklmnopqrstuvwxyzAB")]
    [InlineData("github_pat_11AABBCC0abcdefghijklmnopqrstuvwxyz_abcdefghijklmnopqrstuvwxyz1234567890")]
    [InlineData("ghp-1234567890abcdefghijklmnopqrstuvwxyzAB")]
    public void RedactText_RedactsGitHubTokenPrefixes(string token)
    {
        var result = SecretRedactor.RedactText("token=" + token)!;

        Assert.Equal("token=[REDACTED]", result);
        Assert.DoesNotContain(token, result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactJsonNode_ConfiguredKeyAndPattern_AreRedacted()
    {
        var settings = new RedactionSettings(
            AttributeKeys: ["customer.ssn"],
            Patterns: [new RedactionPatternSettings("internal-id", @"INC-[0-9]+")],
            UserIdentifierAttributes: []);
        var node = JsonNode.Parse("""
            {
              "customer": { "ssn": "123-45-6789" },
              "message": "failed request INC-1042"
            }
            """)!;

        var redacted = SecretRedactor.RedactJsonNode(node, settings);

        Assert.Equal("[REDACTED]", redacted["customer"]!["ssn"]!.GetValue<string>());
        Assert.Equal("failed request [REDACTED]", redacted["message"]!.GetValue<string>());
    }

    [Fact]
    public void Protect_SameSaltIsStableAndDifferentSaltChangesPseudonym()
    {
        var settings = new RedactionSettings(
            AttributeKeys: ["user.id"],
            Patterns: [],
            UserIdentifierAttributes: ["user.id"]);
        var signal = CreateSignal(JsonNode.Parse("""{"user":{"id":"operator-42"}}""")!);

        var pseudonymizer = new UserIdentifierPseudonymizer(
            Options.Create(new PseudonymizationOptions { Salt = "salt-one" }));
        var first = pseudonymizer.Protect(signal, settings);
        var repeated = new UserIdentifierPseudonymizer(Options.Create(new PseudonymizationOptions { Salt = "salt-one" }))
            .Protect(signal, settings);
        var rotated = new UserIdentifierPseudonymizer(Options.Create(new PseudonymizationOptions { Salt = "salt-two" }))
            .Protect(signal, settings);

        var firstValue = first.Attributes["user"]!["id"]!.GetValue<string>();
        Assert.StartsWith(UserIdentifierPseudonymizer.Prefix, firstValue, StringComparison.Ordinal);
        Assert.Equal(firstValue, repeated.Attributes["user"]!["id"]!.GetValue<string>());
        Assert.NotEqual(firstValue, rotated.Attributes["user"]!["id"]!.GetValue<string>());

        var redacted = SecretRedactor.Redact(first, settings, pseudonymizer.IsCanonicalPseudonym);
        Assert.Equal(firstValue, redacted.Attributes["user"]!["id"]!.GetValue<string>());
        Assert.DoesNotContain("operator-42", redacted.Attributes.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Protect_MissingSaltFailsSafeByRedactingIdentifier()
    {
        var settings = new RedactionSettings([], [], ["user.id"]);
        var signal = CreateSignal(JsonNode.Parse("""{"user":{"id":"operator-42"}}""")!);
        var pseudonymizer = new UserIdentifierPseudonymizer(Options.Create(new PseudonymizationOptions()));

        var protectedSignal = pseudonymizer.Protect(signal, settings);

        Assert.Equal("[REDACTED]", protectedSignal.Attributes["user"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_CraftedPseudonymLikeSensitiveValuesAreRedacted()
    {
        var settings = new RedactionSettings(
            AttributeKeys: ["password", "user.id"],
            Patterns: [],
            UserIdentifierAttributes: ["user.id"]);
        var signal = CreateSignal(JsonNode.Parse("""
            {"password":"[PSEUDONYM:v1:attacker-secret]","user":{"id":"[PSEUDONYM:v1:forged]"}}
            """)!);

        var redacted = SecretRedactor.Redact(signal, settings);

        Assert.Equal("[REDACTED]", redacted.Attributes["password"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", redacted.Attributes["user"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Redact_CanonicalPseudonymCannotBeReplayedAtAnotherSensitivePath()
    {
        var settings = new RedactionSettings(
            AttributeKeys: ["password", "user.id"],
            Patterns: [],
            UserIdentifierAttributes: ["user.id"]);
        var pseudonymizer = new UserIdentifierPseudonymizer(
            Options.Create(new PseudonymizationOptions { Salt = "salt-one" }));
        var protectedSignal = pseudonymizer.Protect(
            CreateSignal(JsonNode.Parse("""{"user":{"id":"operator-42"}}""")!),
            settings);
        var canonicalValue = protectedSignal.Attributes["user"]!["id"]!.GetValue<string>();
        protectedSignal = protectedSignal with
        {
            Attributes = new JsonObject
            {
                ["password"] = canonicalValue,
                ["user"] = new JsonObject { ["id"] = canonicalValue }
            }
        };

        var redacted = SecretRedactor.Redact(
            protectedSignal,
            settings,
            pseudonymizer.IsCanonicalPseudonym);

        Assert.Equal("[REDACTED]", redacted.Attributes["password"]!.GetValue<string>());
        Assert.Equal(canonicalValue, redacted.Attributes["user"]!["id"]!.GetValue<string>());
    }
    private static NormalizedSignal CreateSignal(JsonNode attributes) => new(
        Source: "tester",
        ExternalId: null,
        TraceId: null,
        SpanId: null,
        ParentSpanId: null,
        ServiceName: "payments-api",
        Environment: "test",
        OperationName: null,
        Severity: null,
        ErrorType: "ExampleError",
        ErrorMessage: null,
        Summary: "Example failure",
        Description: null,
        HttpMethod: null,
        HttpRoute: null,
        HttpStatusCode: null,
        DurationMs: null,
        Attributes: attributes,
        Body: new JsonObject(),
        ObservedAtUtc: DateTimeOffset.UtcNow);
}
