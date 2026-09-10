using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Tester;
using IncidentCompass.Tester.Evaluation;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

public sealed class EvaluationRunnerTests
{
    [Fact]
    public async Task TransientPollingRetries_ReachAttemptDeadlineAndRetainEveryFailure()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var outputPath = Path.Combine(directory, "result.json");
            await WarmIngestPathAsync(TestContext.Current.CancellationToken);
            var handler = new DeadlineBoundPollingHandler();
            using var client = CreateClient(handler);
            // The deadline is not a race against anything the runner does: the stub blocks until it
            // fires, so it only has to outlast an in-memory ingest round trip that measures in
            // microseconds. It is sized against scheduler stalls on a contended machine, not against
            // work, because an attempt cancelled while ingesting never reaches the polling phase.
            var runner = new EvaluationRunner(client, CreateOptions(outputPath, TimeSpan.FromMilliseconds(150)));

            var run = runner.RunAsync(TestContext.Current.CancellationToken);
            await handler.PollingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Equal(5 * EvaluationCorpus.RequiredAttemptsPerCase, handler.IncidentPosts);
            Assert.True(handler.FaultReads >=
                handler.IncidentPosts * (DeadlineBoundPollingHandler.TransientReadsPerAttempt + 1));
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
            Assert.Equal(2, result.RootElement.GetProperty("schemaVersion").GetInt32());
            var attempts = result.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
            Assert.Equal(handler.IncidentPosts, attempts.Length);
            Assert.All(attempts, static attempt =>
            {
                Assert.Equal("wait_for_terminal", attempt.GetProperty("lastObservedPhase").GetString());
                Assert.NotEqual(Guid.Empty, attempt.GetProperty("faultId").GetGuid());
                Assert.NotEqual(Guid.Empty, attempt.GetProperty("jobId").GetGuid());
                Assert.Contains("attempt deadline exceeded", attempt.GetProperty("failureDetail").GetString() ?? string.Empty, StringComparison.Ordinal);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExhaustedTransientPollingRetries_ReportTheUpstreamStatusInsteadOfTheDeadline()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var outputPath = Path.Combine(directory, "retry-cap-result.json");
            var handler = new TransientPollingFailureHandler();
            using var client = CreateClient(handler);
            // The retry cap is a per-read bound, so one attempt per case proves it as well as three.
            // The poll interval only paces the capped retries here, and a platform with coarse timer
            // granularity charges a full timer tick for each of them, so this test drops the pacing
            // rather than paying nine ticks per attempt for a bound that counts reads.
            var runner = new EvaluationRunner(client, CreateOptions(
                outputPath,
                TimeSpan.FromSeconds(10),
                runsPerCase: 1,
                pollInterval: TimeSpan.Zero));

            var run = runner.RunAsync(TestContext.Current.CancellationToken);
            await handler.PollingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Equal(5, handler.IncidentPosts);
            Assert.True(handler.FaultReads >= handler.IncidentPosts * TransientHttpRetry.MaxAttempts);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
            var attempts = result.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
            Assert.Equal(handler.IncidentPosts, attempts.Length);
            Assert.All(attempts, static attempt =>
            {
                Assert.Equal("wait_for_terminal", attempt.GetProperty("lastObservedPhase").GetString());
                Assert.Equal("HttpRequestException: HTTP status 500", attempt.GetProperty("failureDetail").GetString());
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellableHangingPollingAndRecovery_StayWithinIndependentWatchdog()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var outputPath = Path.Combine(directory, "hanging-read-result.json");
            var handler = new CancellableHangingReadHandler();
            using var client = CreateClient(handler);
            var runner = new EvaluationRunner(client, CreateOptions(outputPath, TimeSpan.FromMilliseconds(50)));

            var run = runner.RunAsync(TestContext.Current.CancellationToken);
            await handler.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
            var attempts = result.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
            Assert.Equal(5 * EvaluationCorpus.RequiredAttemptsPerCase, attempts.Length);
            Assert.True(handler.Reads >= attempts.Length * 2);
            Assert.All(attempts, static attempt =>
                Assert.Contains(
                    "attempt deadline exceeded",
                    attempt.GetProperty("failureDetail").GetString() ?? string.Empty,
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TopLevelCancellationConcurrentWithHealthFailure_CheckpointsAndNormalizesCancellation()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var outputPath = Path.Combine(directory, "health-cancelled-result.json");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var client = CreateClient(new CancelledHealthFailureHandler(cancellation));
            var runner = new EvaluationRunner(client, CreateOptions(outputPath, TimeSpan.FromSeconds(10)));

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

            Assert.Equal(cancellation.Token, exception.CancellationToken);
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
            Assert.Empty(result.RootElement.GetProperty("attempts").EnumerateArray());
            Assert.DoesNotContain("provider-body-secret", result.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalCancellation_CheckpointsPartialAttemptAndTerminates()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var outputPath = Path.Combine(directory, "cancelled-result.json");
            using var client = CreateClient(new CancellationBlockingHandler());
            var runner = new EvaluationRunner(client, CreateOptions(outputPath, TimeSpan.FromSeconds(10)));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
            var attempt = Assert.Single(result.RootElement.GetProperty("attempts").EnumerateArray());
            Assert.Equal("ingest_cancelled", attempt.GetProperty("lastObservedPhase").GetString());
            Assert.Equal("external cancellation requested", attempt.GetProperty("failureDetail").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalCancellationConcurrentWithTransportFailure_RetainsPriorAndCurrentAttempts()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var outputPath = Path.Combine(directory, "transport-cancelled-result.json");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var client = CreateClient(new CompletedThenTransportCancellationHandler(cancellation));
            var runner = new EvaluationRunner(client, CreateOptions(outputPath, TimeSpan.FromSeconds(10)));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
            var attempts = result.RootElement.GetProperty("attempts").EnumerateArray().ToArray();
            Assert.Equal(2, attempts.Length);
            Assert.True(attempts[0].GetProperty("completion").GetProperty("passed").GetBoolean());
            Assert.Equal("ingest_cancelled", attempts[1].GetProperty("lastObservedPhase").GetString());
            Assert.Equal("external cancellation requested", attempts[1].GetProperty("failureDetail").GetString());
            Assert.DoesNotContain("provider-body-secret", result.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CriteriaEvaluation_UsesOnlyRetainedSanitizedSnapshot()
    {
        var knownCase = EvaluationCorpus.Load(Path.Combine(
            RepositoryRootLocator.Find(),
            "evaluations",
            "triage",
            "corpus-v1.json")).Cases.Single(static item => item.Kind == "known");
        var beyondCutoff = EvaluationReportSnapshotFactory.Create(CreateReport(
            new string('x', EvaluationReportSnapshotFactory.MaximumSummaryLength) + " inventory",
            "Review the incident.",
            []));
        var redactedMatch = EvaluationReportSnapshotFactory.Create(CreateReport(
            "{\"password\":\"inventory\"}",
            "Review the incident.",
            []));
        var truncatedCredential = EvaluationReportSnapshotFactory.Create(CreateReport(
            new string('x', EvaluationReportSnapshotFactory.MaximumSummaryLength - 20) +
            "{\"password\":\"" + new string('s', 1000) + "\"}",
            "Review the incident.",
            []));
        var evidence = Enumerable.Range(1, EvaluationReportSnapshotFactory.MaximumEvidenceItems + 1)
            .Select(index => Evidence(index, index > EvaluationReportSnapshotFactory.MaximumEvidenceItems
                ? "RetrievedItem"
                : "Signal"))
            .ToArray();
        var evidenceBeyondPrefix = EvaluationReportSnapshotFactory.Create(CreateReport(
            "No authored diagnosis term is present.",
            "Review the incident.",
            evidence));

        Assert.False(EvaluationRunner.EvaluateDiagnosis(knownCase, beyondCutoff).Passed);
        Assert.False(EvaluationRunner.EvaluateDiagnosis(knownCase, redactedMatch).Passed);
        Assert.Equal("[REDACTED:TRUNCATED_CREDENTIAL]", truncatedCredential!.Summary);
        Assert.False(EvaluationRunner.EvaluateDiagnosis(knownCase, truncatedCredential).Passed);
        Assert.False(EvaluationRunner.EvaluateEvidence(knownCase, evidenceBeyondPrefix, currentAttemptPublication: true).Passed);
        Assert.Equal(1, evidenceBeyondPrefix!.OmittedEvidenceCount);
    }

    [Fact]
    public void ReportSnapshot_PreservesScoredTextAndSafeEvidenceMetadataOnly()
    {
        using var payload = JsonDocument.Parse("""{"rawPrompt":"do not retain","providerBody":"do not retain either"}""");
        var evidence = Enumerable.Range(1, 25).Select(index => new TriageEvidenceResponse(
            Guid.NewGuid(),
            "citation",
            Guid.NewGuid(),
            "https://operator:credential@evidence/" + index + "?access_token=reference-secret-" + index,
            "quote-secret-" + index,
            0.75,
            "RetrievedItem",
            "refresh_token=artifact-secret-" + index + "; x-api-key=header-secret-" + index,
            payload.RootElement.Clone())).ToArray();
        var report = new TriageReportResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Completed",
            "Inventory timeout diagnosis. password=summary-secret {\"password\":\"json-secret\"} access_token=query-secret\r\n" + new string('x', 3000),
            "KnownIncident",
            "High",
            false,
            "Inspect checkout. Authorization: Bearer action-secret",
            ["password=limitation-secret"],
            "config-hash",
            DateTimeOffset.UtcNow,
            evidence,
            "Current");

        var snapshot = Assert.IsType<EvaluationReportSnapshot>(EvaluationReportSnapshotFactory.Create(report));

        Assert.Equal(report.Id, snapshot.ReportId);
        Assert.Equal(report.Status, snapshot.Status);
        Assert.Contains("Inventory timeout diagnosis", snapshot.Summary, StringComparison.Ordinal);
        Assert.Equal(EvaluationReportSnapshotFactory.MaximumSummaryLength, snapshot.Summary.Length);
        Assert.Equal(EvaluationReportSnapshotFactory.MaximumEvidenceItems, snapshot.Evidence.Count);
        Assert.Equal(5, snapshot.OmittedEvidenceCount);
        Assert.Single(snapshot.Limitations);
        Assert.Equal(0, snapshot.OmittedLimitationCount);
        Assert.All(snapshot.Evidence, static item =>
        {
            Assert.NotEqual(Guid.Empty, item.EvidenceId);
            Assert.NotEqual(Guid.Empty, item.ArtifactId);
            Assert.Equal("RetrievedItem", item.ArtifactKind);
        });
        var serialized = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("summary-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("json-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("action-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("artifact-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("reference-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("limitation-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("quote-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("rawPrompt", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("providerBody", serialized, StringComparison.Ordinal);
    }

    private static EvaluationOptions CreateOptions(
        string outputPath,
        TimeSpan attemptTimeout,
        int runsPerCase = EvaluationCorpus.RequiredAttemptsPerCase,
        TimeSpan? pollInterval = null) =>
        new(
            new Uri("http://tester/"),
            TimeSpan.FromSeconds(5),
            attemptTimeout,
            TimeSpan.FromMilliseconds(20),
            pollInterval ?? TimeSpan.FromMilliseconds(1),
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

    // The attempt deadline covers ingest as well as polling, so the first attempt of a run would
    // otherwise pay one-time JIT and source-generated serializer initialization for the whole ingest
    // path inside that deadline. On a cold, contended machine that alone costs tens of milliseconds
    // and ends the attempt in the ingest phase instead of while polling. Warming the same path first
    // leaves every measured attempt doing steady-state work. The warm-up uses a throwaway client so
    // the counters of the handler under test stay untouched.
    private static async Task WarmIngestPathAsync(CancellationToken cancellationToken)
    {
        var corpus = EvaluationCorpus.Load(Path.Combine(
            RepositoryRootLocator.Find(),
            "evaluations",
            "triage",
            "corpus-v1.json"));
        using var client = CreateClient(new DeadlineBoundPollingHandler());
        using var response = await client.PostAsJsonAsync(
            "api/v1/incidents",
            corpus.Cases[0].CreateEnvelope("warm-up", 1),
            TesterJsonContext.Default.IncidentEnvelope,
            cancellationToken);
        _ = await response.Content.ReadFromJsonAsync(
            TesterJsonContext.Default.IngestSignalResponse,
            cancellationToken);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) =>
        new(handler)
        {
            BaseAddress = new Uri("http://tester/"),
            Timeout = TimeSpan.FromSeconds(5)
        };

    private static TriageReportResponse CreateReport(
        string summary,
        string recommendedAction,
        IReadOnlyList<TriageEvidenceResponse> evidence) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Completed",
            summary,
            "KnownIncident",
            "High",
            false,
            recommendedAction,
            [],
            "test-config",
            DateTimeOffset.UtcNow,
            evidence,
            "Current");

    private static TriageEvidenceResponse Evidence(int index, string artifactKind)
    {
        using var payload = JsonDocument.Parse("{}");
        return new TriageEvidenceResponse(
            Guid.NewGuid(),
            "citation",
            Guid.NewGuid(),
            "evidence:" + index,
            null,
            0.5,
            artifactKind,
            "artifact:" + index,
            payload.RootElement.Clone());
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "incidentcompass-evaluation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    // A polling attempt has two legitimate terminal bounds: the attempt deadline, and the transient
    // retry cap in TransientHttpRetry, which deliberately propagates the real upstream failure on
    // its last attempt. A stub that only ever answers transient failures lets those two bounds race
    // - the cap needs ten in-memory reads plus nine poll delays, and whether that fits inside a
    // short attempt deadline depends on the platform timer granularity - so the reported terminal
    // failure would vary by machine. This stub answers a bounded number of transient failures per
    // attempt, which exercises the retry path, and then blocks every later read until the request is
    // cancelled. The cap becomes unreachable, so the attempt deadline is the only way out.
    private sealed class DeadlineBoundPollingHandler : HttpMessageHandler
    {
        public const int TransientReadsPerAttempt = 2;

        private int readsForCurrentAttempt;

        public TaskCompletionSource PollingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int IncidentPosts { get; private set; }
        public int FaultReads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/health")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post && path == "/api/v1/incidents")
            {
                IncidentPosts++;
                readsForCurrentAttempt = 0;
                return JsonResponse(new IngestSignalResponse(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    true,
                    true,
                    false,
                    Guid.NewGuid(),
                    "test-config"));
            }

            if (request.Method == HttpMethod.Get && path is not null && path.StartsWith("/api/v1/faults/", StringComparison.Ordinal))
            {
                FaultReads++;
                readsForCurrentAttempt++;
                PollingStarted.TrySetResult();
                if (readsForCurrentAttempt <= TransientReadsPerAttempt)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            throw new InvalidOperationException("Unexpected evaluator route: " + path);
        }

        private static HttpResponseMessage JsonResponse(IngestSignalResponse value) =>
            new(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(value, TesterJsonContext.Default.IngestSignalResponse)
            };
    }

    // Every fault read fails transiently, so the retry cap is the terminal bound. Pair it with an
    // attempt deadline far above the capped retry window, never with one the cap can race.
    private sealed class TransientPollingFailureHandler : HttpMessageHandler
    {
        public TaskCompletionSource PollingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int IncidentPosts { get; private set; }
        public int FaultReads { get; private set; }

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
                IncidentPosts++;
                return Task.FromResult(JsonResponse(new IngestSignalResponse(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    true,
                    true,
                    false,
                    Guid.NewGuid(),
                    "test-config")));
            }

            if (request.Method == HttpMethod.Get && path is not null && path.StartsWith("/api/v1/faults/", StringComparison.Ordinal))
            {
                FaultReads++;
                PollingStarted.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            throw new InvalidOperationException("Unexpected evaluator route: " + path);
        }

        private static HttpResponseMessage JsonResponse(IngestSignalResponse value) =>
            new(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(value, TesterJsonContext.Default.IngestSignalResponse)
            };
    }

    private sealed class CancellationBlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/v1/health")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation-blocking request unexpectedly completed.");
        }
    }

    private sealed class CancelledHealthFailureHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("provider-body-secret")
            });
        }
    }

    private sealed class CancellableHangingReadHandler : HttpMessageHandler
    {
        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Reads { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/health")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Post && path == "/api/v1/incidents")
            {
                return JsonResponse(new IngestSignalResponse(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    true,
                    true,
                    false,
                    Guid.NewGuid(),
                    "test-config"));
            }

            if (request.Method == HttpMethod.Get && path is not null && path.StartsWith("/api/v1/faults/", StringComparison.Ordinal))
            {
                Reads++;
                FirstReadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            throw new InvalidOperationException("Unexpected evaluator route: " + path);
        }

        private static HttpResponseMessage JsonResponse(IngestSignalResponse value) =>
            new(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(value, TesterJsonContext.Default.IngestSignalResponse)
            };
    }

    private sealed class CompletedThenTransportCancellationHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        private readonly Guid faultId = Guid.NewGuid();
        private readonly Guid jobId = Guid.NewGuid();
        private readonly Guid reportId = Guid.NewGuid();
        private int incidentPosts;

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
                incidentPosts++;
                if (incidentPosts > 1)
                {
                    cancellation.Cancel();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("provider-body-secret")
                    });
                }

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
                        "Open",
                        new TriageJobSummary(jobId, "Succeeded", 1, "test-config", DateTimeOffset.UtcNow)),
                    TesterJsonContext.Default.FaultDetailsResponse));
            }

            if (request.Method == HttpMethod.Get && path == $"/api/v1/faults/{faultId}/ledger")
            {
                return Task.FromResult(JsonResponse(
                    new FaultLedgerResponse(faultId,
                    [
                        new FaultLedgerEvent(
                            1,
                            jobId,
                            1,
                            "ReportPublished",
                            null,
                            null,
                            null,
                            null,
                            "report:" + reportId,
                            "test-config")
                    ]),
                    TesterJsonContext.Default.FaultLedgerResponse));
            }

            if (request.Method == HttpMethod.Get && path == $"/api/v1/triage-reports/{reportId}")
            {
                return Task.FromResult(JsonResponse(
                    CreateReport("Inventory timeout diagnosis.", "Inspect checkout.", []) with
                    {
                        Id = reportId,
                        FaultId = faultId
                    },
                    TesterJsonContext.Default.TriageReportResponse));
            }

            throw new InvalidOperationException("Unexpected evaluator route: " + path);
        }

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
