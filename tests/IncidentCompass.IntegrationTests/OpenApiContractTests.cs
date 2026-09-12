using System.Net;
using System.Text.Json;
using IncidentCompass.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IncidentCompass.IntegrationTests;

public sealed class OpenApiContractTests
{
    private static readonly JsonSerializerOptions BaselineJsonOptions = new()
    {
        WriteIndented = true
    };

    // Pure comparison: this never writes the baseline. If the live document has drifted,
    // regenerate it deliberately with scripts\update-openapi-baseline.ps1 and review the diff
    // before committing it.
    [Fact]
    public async Task DevelopmentOpenApiDocument_MatchesCommittedBaseline()
    {
        var normalizedActual = await CaptureNormalizedDocumentAsync(TestContext.Current.CancellationToken);
        var expected = await File.ReadAllTextAsync(BaselinePath(), TestContext.Current.CancellationToken);
        var normalizedExpected = NormalizeJson(expected);

        if (!string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal))
        {
            Assert.Fail(
                $"The live OpenAPI document no longer matches the committed baseline at {BaselinePath()}. " +
                "If this API surface change is intentional, run scripts\\update-openapi-baseline.ps1 to " +
                "regenerate the baseline, then review the diff before committing it.");
        }
    }

    // Deliberate, explicit regeneration path: an xUnit "explicit" fact is never picked up by a
    // normal test run (see the xUnit -explicit filtering docs), so this only executes when
    // scripts\update-openapi-baseline.ps1 targets it directly. This keeps the comparison test
    // above a pure read-only check with no environment-variable branch and no [CallerFilePath]
    // self-write.
    [Fact(Explicit = true)]
    public async Task RegenerateOpenApiBaseline()
    {
        var normalized = await CaptureNormalizedDocumentAsync(TestContext.Current.CancellationToken);
        // The repository requires LF line endings (core.eol=lf); Environment.NewLine on Windows
        // would append a trailing CRLF and fail `dotnet format --verify-no-changes`.
        await File.WriteAllTextAsync(BaselinePath(), normalized + "\n", TestContext.Current.CancellationToken);
    }

    private static async Task<string> CaptureNormalizedDocumentAsync(CancellationToken cancellationToken)
    {
        using var developmentFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseExplicitMockProviders();
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });
        using var client = developmentFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/openapi/v1.json", cancellationToken);
        var actual = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return NormalizeJson(actual);
    }

    private static string BaselinePath() =>
        Path.Combine(RepositoryRootLocator.Find(), "tests", "IncidentCompass.IntegrationTests", "Baselines", "openapi-v1.json");

    private static string NormalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var serialized = JsonSerializer.Serialize(document.RootElement, BaselineJsonOptions);

        // Two newlines have to be normalized here and only one of them is about formatting.
        // System.Text.Json's indented writer emits Environment.NewLine between lines, which is
        // CRLF on Windows, while the repository requires LF (core.eol=lf).
        //
        // Separately, a multi-line XML documentation summary carries the newline of the generated
        // documentation file into the description it produces, and the serializer escapes it as a
        // two-character sequence inside the string value. Escaped, it survives a plain CRLF
        // replacement, so a baseline regenerated on Windows could never match the document
        // generated on Linux. That asymmetry is invisible on either platform alone and shows up
        // only when the two meet, which here meant CI.
        return serialized
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\\r\\n", "\\n", StringComparison.Ordinal);
    }
}
