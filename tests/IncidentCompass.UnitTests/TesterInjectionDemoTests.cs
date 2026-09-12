using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Intake.Fingerprinting;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Tester;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

public sealed class TesterInjectionDemoTests
{
    [Fact]
    public void SharedFixture_IsHashPinnedAndMappedWithoutDuplicatingItsInstruction()
    {
        var root = RepositoryRootLocator.Find();
        var fixturePath = Path.Combine(root, "samples", "incidents", "tester-ticket-action-injection.json");
        var bytes = File.ReadAllBytes(fixturePath);
        Assert.Equal(DemoInjectionScenarioLoader.ExpectedSha256, Convert.ToHexString(SHA256.HashData(bytes)));

        using var document = JsonDocument.Parse(bytes);
        var scenario = DemoInjectionScenarioLoader.Load(fixturePath);
        var envelope = Assert.IsType<IncidentEnvelope>(scenario.ExactEnvelope);
        Assert.Equal("5", scenario.Id);
        Assert.True(scenario.RequiresNoActionGate);
        Assert.Equal(document.RootElement.GetProperty("attributes").GetProperty("errorMessage").GetString(), envelope.Attributes.ErrorMessage);
        Assert.Equal(document.RootElement.GetProperty("correlation").GetProperty("externalId").GetString(), envelope.Correlation.ExternalId);
        Assert.Same(envelope, scenario.CreateEnvelope("ignored", 1));
        var outputEnvelope = Assert.IsType<IncidentEnvelope>(DemoInjectionScenarioLoader.Load().ExactEnvelope);
        Assert.Equal(envelope.SourceKind, outputEnvelope.SourceKind);
        Assert.Equal(envelope.Attributes, outputEnvelope.Attributes);
        Assert.Equal(envelope.Correlation, outputEnvelope.Correlation);

        AssertFixtureCopyContract(
            Path.Combine(root, "src", "IncidentCompass.Tester", "IncidentCompass.Tester.csproj"));
        AssertFixtureCopyContract(
            Path.Combine(root, "tests", "IncidentCompass.IntegrationTests", "IncidentCompass.IntegrationTests.csproj"));
    }

    [Fact]
    public void CreateAll_PinsScenarioCatalogAndVariesFingerprintButNotSummaryPerEnvelope()
    {
        var scenarios = DemoScenario.CreateAll("run");
        Assert.Collection(
            scenarios,
            item => AssertScenario(item, "1", "known-timeout-runbook", 1, "KnownIncident", false, "Runbook"),
            item => AssertScenario(item, "2", "unknown-null-reference", 1, "Unknown", false, null),
            item => AssertScenario(item, "3", "provider-unavailable-flood", 6, "SimpleKnownError", true, null),
            item => AssertScenario(item, "4", "validation-noise", 1, "Noise", false, null));
        var firstKnownEnvelope = scenarios[0].CreateEnvelope("first-run", 1);
        var secondKnownEnvelope = scenarios[0].CreateEnvelope("second-run", 1);
        Assert.Equal("checkout-api", firstKnownEnvelope.ServiceName);
        Assert.Equal("TimeoutException", firstKnownEnvelope.Attributes.ErrorType);
        Assert.Equal("POST /checkout", firstKnownEnvelope.Attributes.OperationName);
        Assert.Matches("^/checkout/[g-z]{16}$", firstKnownEnvelope.Attributes.HttpRoute);
        Assert.NotEqual(firstKnownEnvelope.Attributes.HttpRoute, secondKnownEnvelope.Attributes.HttpRoute);
        Assert.Equal(
            Summary(firstKnownEnvelope),
            Summary(secondKnownEnvelope));
        Assert.NotEqual(
            Fingerprint(firstKnownEnvelope),
            Fingerprint(secondKnownEnvelope));
    }

    [Fact]
    public async Task InjectionScenario_NoLifecycleEventsPassesAfterBoundedLedgerReads()
    {
        var handler = new InjectionDemoHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://tester/") };
        var tester = new DemoTester(client, TestOptions());

        var exitCode = await tester.RunScenariosAsync(
            [LoadScenario()],
            "ignored",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(1 + DemoTester.ActionObservationReadCount, handler.LedgerReads);
        Assert.Equal(1, handler.IncidentPosts);
        Assert.Equal(1, handler.ReportReads);
    }

    [Theory]
    [InlineData("ActionProposed")]
    [InlineData("ApprovalDecision")]
    [InlineData("ActionDispatchStarted")]
    [InlineData("ActionCompleted")]
    public async Task InjectionScenario_LifecycleEventFailsNonzero(string eventType)
    {
        var handler = new InjectionDemoHandler(eventType);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://tester/") };
        var tester = new DemoTester(client, TestOptions());

        var exitCode = await tester.RunScenariosAsync(
            [LoadScenario()],
            "ignored",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(2, handler.LedgerReads);
        Assert.Equal(1, handler.IncidentPosts);
        Assert.Equal(1, handler.ReportReads);
    }

    [Fact]
    public async Task InjectionScenario_NonTransientLedgerErrorFailsWithoutRetryingForever()
    {
        var handler = new InjectionDemoHandler(failLedger: true);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://tester/") };
        var tester = new DemoTester(client, TestOptions());

        var exitCode = await tester.RunScenariosAsync(
            [LoadScenario()],
            "ignored",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, handler.LedgerReads);
    }

    private static DemoScenario LoadScenario() =>
        DemoInjectionScenarioLoader.Load(Path.Combine(
            RepositoryRootLocator.Find(),
            "samples",
            "incidents",
            "tester-ticket-action-injection.json"));

    private static TesterOptions TestOptions() =>
        new(
            new Uri("http://tester/"),
            new Uri("http://public/"),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5));

    private static void AssertScenario(
        DemoScenario scenario,
        string id,
        string name,
        int signalCount,
        string classification,
        bool isMassIssue,
        string? evidenceKind)
    {
        Assert.Equal(id, scenario.Id);
        Assert.Equal(name, scenario.Name);
        Assert.Equal(signalCount, scenario.SignalCount);
        Assert.Equal(classification, scenario.ExpectedClassification);
        Assert.Equal(isMassIssue, scenario.ExpectedIsMassIssue);
        Assert.Equal(evidenceKind, scenario.ExpectedEvidenceKind);
        Assert.False(scenario.RequiresNoActionGate);
        Assert.Null(scenario.ExactEnvelope);
    }

    private static void AssertFixtureCopyContract(string projectPath)
    {
        var project = File.ReadAllText(projectPath);
        Assert.Contains("tester-ticket-action-injection.json", project, StringComparison.Ordinal);
        Assert.Contains("CopyToOutputDirectory", project, StringComparison.Ordinal);
    }

    private static string Summary(IncidentEnvelope envelope) =>
        SummarySynthesizer.ForStructuredSignal(
            envelope.ServiceName,
            envelope.Attributes.OperationName,
            envelope.Attributes.HttpRoute,
            envelope.Attributes.ErrorType,
            envelope.Attributes.ErrorMessage);

    private static string Fingerprint(IncidentEnvelope envelope) =>
        FingerprintCalculator.Compute(
            new NormalizedSignal(
                envelope.SourceKind,
                envelope.Correlation.ExternalId,
                envelope.Correlation.TraceId,
                envelope.Correlation.SpanId,
                null,
                envelope.ServiceName,
                envelope.Environment,
                envelope.Attributes.OperationName,
                envelope.Severity,
                envelope.Attributes.ErrorType,
                envelope.Attributes.ErrorMessage,
                Summary(envelope),
                null,
                null,
                envelope.Attributes.HttpRoute,
                envelope.Attributes.HttpStatusCode,
                null,
                new JsonObject(),
                new JsonObject(),
                envelope.ObservedAtUtc),
            1).Value;

    private sealed class InjectionDemoHandler : HttpMessageHandler
    {
        private readonly string? lifecycleEventType;
        private readonly bool failLedger;
        private readonly Guid faultId = Guid.NewGuid();
        private readonly Guid jobId = Guid.NewGuid();
        private readonly Guid reportId = Guid.NewGuid();

        public InjectionDemoHandler(string? lifecycleEventType = null, bool failLedger = false)
        {
            this.lifecycleEventType = lifecycleEventType;
            this.failLedger = failLedger;
        }

        public int IncidentPosts { get; private set; }

        public int LedgerReads { get; private set; }

        public int ReportReads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/api/v1/incidents")
            {
                IncidentPosts++;
                return Task.FromResult(JsonResponse(
                    new IngestSignalResponse(Guid.NewGuid(), faultId, true, true, false, jobId, "fixture-config"),
                    TesterJsonContext.Default.IngestSignalResponse));
            }

            if (request.Method == HttpMethod.Get && path == $"/api/v1/faults/{faultId}/ledger")
            {
                LedgerReads++;
                if (failLedger)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
                }

                return Task.FromResult(JsonResponse(
                    LedgerResponse(includeLifecycleEvent: LedgerReads > 1),
                    TesterJsonContext.Default.FaultLedgerResponse));
            }

            if (request.Method == HttpMethod.Get && path == $"/api/v1/triage-reports/{reportId}")
            {
                ReportReads++;
                return Task.FromResult(JsonResponse(
                    new TriageReportResponse(
                        reportId,
                        faultId,
                        "Completed",
                        "fixture report",
                        "SimpleKnownError",
                        "Medium",
                        false,
                        "No external action.",
                        [],
                        "fixture-config",
                        DateTimeOffset.UtcNow,
                        []),
                    TesterJsonContext.Default.TriageReportResponse));
            }

            throw new InvalidOperationException("Tester attempted an unexpected HTTP route: " + path);
        }

        private FaultLedgerResponse LedgerResponse(bool includeLifecycleEvent)
        {
            var events = new List<FaultLedgerEvent>
            {
                Event(1, "ReportPublished", "report:" + reportId)
            };
            if (includeLifecycleEvent && lifecycleEventType is not null)
            {
                events.Add(Event(2, lifecycleEventType, "action:" + Guid.NewGuid()));
            }

            return new FaultLedgerResponse(faultId, events);
        }

        private FaultLedgerEvent Event(long id, string eventType, string payloadRef) =>
            new(id, jobId, 1, eventType, null, null, null, null, payloadRef, "fixture-config");

        private static HttpResponseMessage JsonResponse<T>(
            T value,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value, jsonTypeInfo)
            };
    }
}
