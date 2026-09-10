using System.Globalization;
using System.Text.Json;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.UnitTests;

public sealed class MemoryDocumentationStatusTests
{
    private static readonly string[] ExpectedDocumentationStatuses =
        ["Current", "Stale", "Unversioned", "ServiceMismatch"];

    [Fact]
    public async Task ExecuteAsync_AnnotatesEveryRetrievedArtifactAndOutputItem()
    {
        var configuration = ConfigurationFor("checkout", "2026.07.13.2");
        var matches = new[]
        {
            Match("checkout", "2026.07.13.2"),
            Match("checkout", "2026.07.13.1"),
            Match("checkout", null),
            Match("orders", "2026.07.13.2")
        };
        var tool = new MemorySearchTool(
            new StaticEmbeddingClient(),
            new StaticMemoryRepository(matches));
        var validation = tool.Validate(Arguments("checkout timeout"));

        var result = await tool.ExecuteAsync(
            Context(configuration, "checkout"),
            validation.SanitizedArguments,
            TestContext.Current.CancellationToken);

        var items = result.Output.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(ExpectedDocumentationStatuses,
            items.Select(static item => item.GetProperty("documentationStatus").GetString()));
        Assert.All(items, item => Assert.Equal(
            "2026.07.13.2",
            item.GetProperty("targetCurrentRelease").GetString()));

        Assert.NotNull(result.Artifacts);
        var drafts = result.Artifacts.ToArray();
        Assert.Equal(4, drafts.Length);
        Assert.Equal(ExpectedDocumentationStatuses,
            drafts.Select(static draft => draft.Payload["documentationStatus"]!.GetValue<string>()));
        Assert.All(drafts, draft => Assert.Equal(
            "2026.07.13.2",
            draft.Payload["targetCurrentRelease"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("checkout", "checkout", "2026.07.13.2", "2026.07.13.2", "Current")]
    [InlineData("checkout", "checkout", "2026.07.13.1", "2026.07.13.2", "Stale")]
    [InlineData("checkout", "checkout", null, "2026.07.13.2", "Unversioned")]
    [InlineData("checkout", "orders", "2026.07.13.2", "2026.07.13.2", "ServiceMismatch")]
    [InlineData("unknown", "unknown", "2026.07.13.2", "2026.07.13.2", "ServiceMismatch")]
    public void Assess_DoesNotLabelMissingOrUnknownMarkersAsCurrent(
        string faultService,
        string itemService,
        string? itemRelease,
        string markerRelease,
        string expectedStatus)
    {
        var assessment = MemoryDocumentationStatusEvaluator.Assess(
            ConfigurationFor(faultService, markerRelease),
            faultService,
            Match(itemService, itemRelease));

        Assert.Equal(expectedStatus, assessment.Status.ToString());
        if (!string.Equals(expectedStatus, "Current", StringComparison.Ordinal))
        {
            Assert.NotEqual(MemoryDocumentationStatus.Current, assessment.Status);
        }
    }

    [Fact]
    public void Assess_MissingFaultServiceMarkerIsUnversioned()
    {
        var assessment = MemoryDocumentationStatusEvaluator.Assess(
            TestTriageConfiguration.Create(),
            "checkout",
            Match("checkout", "2026.07.13.2"));

        Assert.Null(assessment.TargetCurrentRelease);
        Assert.Equal(MemoryDocumentationStatus.Unversioned, assessment.Status);
    }

    private static TriageConfiguration ConfigurationFor(string service, string release)
    {
        return TestTriageConfiguration.Create() with
        {
            CurrentReleases = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [service] = release
            }
        };
    }

    private static AgentToolExecutionContext Context(TriageConfiguration configuration, string faultService)
    {
        var now = DateTimeOffset.Parse("2026-07-13T00:00:00Z", CultureInfo.InvariantCulture);
        return new AgentToolExecutionContext(
            new TriageJob(
                Guid.NewGuid(),
                Guid.NewGuid(),
                TriageJobStatus.Processing,
                1,
                null,
                null,
                null,
                null,
                null,
                configuration.ConfigHash,
                now,
                now),
            configuration,
            "memory",
            "memory_search",
            "tenant-a",
            faultService);
    }

    private static JsonElement Arguments(string query)
    {
        using var document = JsonDocument.Parse($$"""{"query":"{{query}}"}""");
        return document.RootElement.Clone();
    }

    private static MemorySearchMatch Match(string? service, string? release)
    {
        return new MemorySearchMatch(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "runbook",
            "memory.md",
            "Checkout timeout",
            0,
            "checkout timeout runbook",
            0.95,
            service,
            "payments",
            release);
    }

    private sealed class StaticEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new EmbeddingResponse([1f, 0f], "memory-model", "mock", 1, null));
        }
    }

    private sealed class StaticMemoryRepository(IReadOnlyList<MemorySearchMatch> matches) : IMemoryRepository
    {
        public Task<IReadOnlyList<MemorySearchMatch>> SearchAsync(
            MemorySearchRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(matches);
        }

        public Task<bool> SeedItemExistsAsync(
            string owner,
            MemorySeedItem item,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task ReconcileSeedCorpusAsync(MemorySeedCorpus corpus, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<MemoryCorpusInventory> GetCorpusInventoryAsync(
            string tenantId,
            string owner,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
