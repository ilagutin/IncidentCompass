using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Tester;
using IncidentCompass.Tester.Evaluation;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

public sealed class EvaluationTerminalOutcomeTests
{
    [Fact]
    public async Task DeadLetteredAttempt_RecordsTheJobRowsBoundedErrorCodeAndBothProviderIdentities()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var outputPath = Path.Combine(directory, "dead-lettered-result.json");
            var handler = new DeadLetteredTerminalHandler();
            using var client = CreateClient(handler);
            // One attempt per case is enough: every attempt observes the same terminal job row, and
            // nothing here waits on a deadline, so the stub decides the outcome rather than a clock.
            var runner = new EvaluationRunner(client, CreateOptions(outputPath, runsPerCase: 1));

            var exitCode = await runner.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
            Assert.Equal(3, result.RootElement.GetProperty("schemaVersion").GetInt32());
            var attempts = result.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
            Assert.Equal(5, attempts.Length);
            Assert.All(attempts, static attempt =>
            {
                var completion = attempt.GetProperty("completion");
                Assert.False(completion.GetProperty("passed").GetBoolean());
                Assert.Equal("DeadLettered", completion.GetProperty("jobStatus").GetString());
                Assert.Equal(
                    DeadLetteredTerminalHandler.ErrorCode,
                    completion.GetProperty("jobLastErrorCode").GetString());
                Assert.Equal(JsonValueKind.Null, completion.GetProperty("jobNextAttemptAtUtc").ValueKind);
                Assert.Equal(
                    "terminal job status DeadLettered",
                    attempt.GetProperty("failureDetail").GetString());

                // The adapter name and the configured provider id are recorded as two facts, and a
                // row that states no configured provider is still a counted call: its latency and
                // tokens stay in the attempt's totals.
                var calls = attempt.GetProperty("modelCalls").EnumerateArray().ToArray();
                Assert.Equal(2, calls.Length);
                Assert.All(calls, static call =>
                    Assert.Equal("openai-compatible", call.GetProperty("provider").GetString()));
                Assert.Equal(
                    DeadLetteredTerminalHandler.ConfiguredProviderId,
                    calls[0].GetProperty("providerId").GetString());
                Assert.Equal(JsonValueKind.Null, calls[1].GetProperty("providerId").ValueKind);
                Assert.Equal(42, attempt.GetProperty("usage").GetProperty("totalTokens").GetInt32());
                Assert.Equal(0, attempt.GetProperty("usage").GetProperty("malformedModelCallRows").GetInt32());
                Assert.Equal(300, attempt.GetProperty("latency").GetProperty("modelCallTotalMilliseconds").GetInt64());
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // The evaluator declares its own copy of the fault endpoint's job summary, and a copy drops every
    // property it does not name. This reads the endpoint's wire shape rather than a round trip of the
    // copy, so it fails if the two codes the endpoint projects stop arriving here.
    [Fact]
    public void FaultResponse_ReadsTheProjectedErrorCodeAndScheduledNextAttempt()
    {
        const string body = """
            {
              "id": "8f1f0c06-0b2a-4a55-9f6f-2f4c0c9a0001",
              "status": "Investigating",
              "job": {
                "id": "8f1f0c06-0b2a-4a55-9f6f-2f4c0c9a0002",
                "status": "RetryPending",
                "attempt": 2,
                "configHash": "config-hash",
                "createdAtUtc": "2026-09-11T10:00:00+00:00",
                "lastErrorCode": "provider_unavailable",
                "nextAttemptAtUtc": "2026-09-11T10:05:00+00:00"
              }
            }
            """;

        var fault = JsonSerializer.Deserialize(body, TesterJsonContext.Default.FaultDetailsResponse);

        var job = Assert.IsType<TriageJobSummary>(fault?.Job);
        Assert.Equal("RetryPending", job.Status);
        Assert.Equal("provider_unavailable", job.LastErrorCode);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 11, 10, 5, 0, TimeSpan.Zero),
            job.NextAttemptAtUtc);
    }

    [Fact]
    public void FaultResponse_ReadsAJobThatStatesNeitherCode()
    {
        const string body = """
            {
              "id": "8f1f0c06-0b2a-4a55-9f6f-2f4c0c9a0003",
              "status": "Investigating",
              "job": {
                "id": "8f1f0c06-0b2a-4a55-9f6f-2f4c0c9a0004",
                "status": "Succeeded",
                "attempt": 1,
                "configHash": "config-hash",
                "createdAtUtc": "2026-09-11T10:00:00+00:00",
                "lastErrorCode": null,
                "nextAttemptAtUtc": null
              }
            }
            """;

        var fault = JsonSerializer.Deserialize(body, TesterJsonContext.Default.FaultDetailsResponse);

        var job = Assert.IsType<TriageJobSummary>(fault?.Job);
        Assert.Null(job.LastErrorCode);
        Assert.Null(job.NextAttemptAtUtc);
    }

    private static EvaluationOptions CreateOptions(string outputPath, int runsPerCase) =>
        new(
            new Uri("http://tester/"),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.Zero,
            Path.Combine(RepositoryRootLocator.Find(), "evaluations", "triage", "corpus-v1.json"),
            "unused-config.json",
            outputPath,
            runsPerCase,
            "test-revision",
            "git-tree:test",
            false,
            new EvaluationConfigurationSnapshot([], new EvaluationOrchestratorBudgetResult(4, 1000, 60, 1)),
            true,
            false);

    private static HttpClient CreateClient(HttpMessageHandler handler) =>
        new(handler)
        {
            BaseAddress = new Uri("http://tester/"),
            Timeout = TimeSpan.FromSeconds(5)
        };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "incidentcompass-evaluation-outcome-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // Answers one dead-lettered job whose row states the bounded error code the backend recorded, and
    // a ledger holding two accounted model calls: one naming the configured provider the route used,
    // one written without that identity. No read blocks, so the attempt ends on the observed terminal
    // state and never on a deadline.
    private sealed class DeadLetteredTerminalHandler : HttpMessageHandler
    {
        public const string ErrorCode = "provider_unavailable";
        public const string ConfiguredProviderId = "local-openai";

        private readonly Guid faultId = Guid.NewGuid();
        private readonly Guid jobId = Guid.NewGuid();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/health")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (request.Method == HttpMethod.Post && path == "/api/v1/incidents")
            {
                return Task.FromResult(JsonResponse(
                    new IngestSignalResponse(Guid.NewGuid(), faultId, true, true, false, jobId, "test-config"),
                    TesterJsonContext.Default.IngestSignalResponse,
                    HttpStatusCode.Created));
            }

            if (request.Method == HttpMethod.Get && path == $"/api/v1/faults/{faultId}")
            {
                return Task.FromResult(JsonResponse(
                    new FaultDetailsResponse(
                        faultId,
                        "Investigating",
                        new TriageJobSummary(
                            jobId,
                            "DeadLettered",
                            1,
                            "test-config",
                            DateTimeOffset.UtcNow,
                            ErrorCode,
                            NextAttemptAtUtc: null)),
                    TesterJsonContext.Default.FaultDetailsResponse));
            }

            if (request.Method == HttpMethod.Get && path == $"/api/v1/faults/{faultId}/ledger")
            {
                return Task.FromResult(JsonResponse(
                    new FaultLedgerResponse(faultId,
                    [
                        ModelCall(1, """
                            {"kind":"analysis","routeId":"analysis","model":"local-model",
                             "provider":"openai-compatible","providerId":"local-openai",
                             "usageSource":"provider","inputTokens":10,"outputTokens":5,
                             "totalTokens":15,"durationMs":100}
                            """),
                        ModelCall(2, """
                            {"kind":"analysis","routeId":"analysis","model":"local-model",
                             "provider":"openai-compatible","usageSource":"provider",
                             "inputTokens":20,"outputTokens":7,"totalTokens":27,"durationMs":200}
                            """)
                    ]),
                    TesterJsonContext.Default.FaultLedgerResponse));
            }

            throw new InvalidOperationException("Unexpected evaluator route: " + path);
        }

        private FaultLedgerEvent ModelCall(long id, string rationale) =>
            new(id, jobId, 1, "ModelCall", "analysis", null, null, rationale, null, "test-config");

        private static HttpResponseMessage JsonResponse<T>(
            T value,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
            HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                Content = JsonContent.Create(value, typeInfo)
            };
    }
}
