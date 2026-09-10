using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Intake.Redaction;
using IncidentCompass.Application.Investigation.Jobs.Testing;
using IncidentCompass.Infrastructure.ModelGateway.Mock;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static IncidentCompass.IntegrationTests.TriageReportGroundingTestSupport;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TriageReportGroundingTests(PostgresRepositoryFixture postgres)
{
    private static readonly string[] NonCitableAnalysisKeyFacts = ["Analysis output is not citable evidence."];

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_WorkerOutputEvidenceIsRejectedThenReprompted()
    {
        using var scope = await CreateScopeAsync(postgres, services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient, WorkerOutputThenTriggerEvidenceModelClient>();
        });
        var ingested = await PostIngestAsync(scope.Client, "worker-output-grounding");

        await RunClaimedJobAsync(scope, ingested.JobId!.Value, "worker-grounding", maxAttempts: 1);

        var jobStatus = await ScalarAsync<string>(scope.ConnectionString, "SELECT status FROM incidentcompass.triage_jobs WHERE id = @job_id;", ("job_id", ingested.JobId.Value));
        var evidenceKinds = await ReadEvidenceKindsAsync(scope.ConnectionString, ingested.FaultId);
        var workerOutputEvidence = await ScalarAsync<long>(scope.ConnectionString, """
            SELECT COUNT(*)
            FROM incidentcompass.triage_evidence e
            JOIN incidentcompass.triage_artifacts a ON a.id = e.artifact_id
            JOIN incidentcompass.triage_reports r ON r.id = e.report_id
            WHERE r.fault_id = @fault_id AND a.kind = 'WorkerOutput';
            """, ("fault_id", ingested.FaultId));

        Assert.Equal("Succeeded", jobStatus);
        Assert.Contains("TriggerSignal", evidenceKinds);
        Assert.Equal(0, workerOutputEvidence);
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_RedactsCraftedMarkersBeforePersistenceArtifactsAndModelRequests()
    {
        const string rawIdentifier = "raw-user-redaction-e2e@example.test";
        const string configuredAttributeSecret = "configured-customer-account-redaction-e2e";
        var crafted = "[PSEUDONYM:v1:" + new string('a', 64) + ":" + new string('b', 64) + "]";
        var requests = new ConcurrentQueue<AiModelRequest>();
        using var scope = await CreateScopeAsync(postgres, services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient>(_ => new CapturingMockAiModelClient(requests));
        });
        var response = await scope.Client.PostAsJsonAsync(
            "/api/v1/incidents",
            new
            {
                sourceKind = "tester",
                serviceName = "redaction-e2e-" + Guid.NewGuid().ToString("N"),
                environment = "prod",
                observedAtUtc = DateTimeOffset.UtcNow,
                attributes = new
                {
                    errorType = "TimeoutException",
                    errorMessage = "request by " + rawIdentifier,
                    httpRoute = "/redaction-e2e",
                    user = new { id = rawIdentifier },
                    password = crafted,
                    customer = new
                    {
                        account = new { id = configuredAttributeSecret }
                    }
                },
                payload = new
                {
                    user = new { email = rawIdentifier },
                    password = crafted
                }
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ingested = await response.Content.ReadFromJsonAsync<TriageReportIngestResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(ingested);
        Assert.NotNull(ingested.JobId);
        await RunClaimedJobAsync(scope, ingested.JobId.Value, "worker-redaction-e2e", 1);

        var signalText = await ScalarAsync<string>(scope.ConnectionString,
            "SELECT concat_ws('|', error_message, summary, attributes::text, body::text) FROM incidentcompass.signals WHERE id = @signal_id;", ("signal_id", ingested.SignalId));
        var triggerPayload = await ScalarAsync<string>(scope.ConnectionString,
            "SELECT redacted_payload::text FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'TriggerSignal';", ("job_id", ingested.JobId.Value));
        var canonicalUserId = await ScalarAsync<string>(scope.ConnectionString,
            "SELECT attributes #>> '{user,id}' FROM incidentcompass.signals WHERE id = @signal_id;", ("signal_id", ingested.SignalId));
        var password = await ScalarAsync<string>(scope.ConnectionString,
            "SELECT attributes->>'password' FROM incidentcompass.signals WHERE id = @signal_id;", ("signal_id", ingested.SignalId));
        var authorization = await ScalarAsync<string>(scope.ConnectionString,
            "SELECT attributes #>> '{customer,account,id}' FROM incidentcompass.signals WHERE id = @signal_id;", ("signal_id", ingested.SignalId));

        Assert.Equal("[REDACTED]", password);
        Assert.Equal("[REDACTED]", authorization);
        Assert.DoesNotContain(rawIdentifier, signalText, StringComparison.Ordinal);
        Assert.DoesNotContain(crafted, signalText, StringComparison.Ordinal);
        Assert.DoesNotContain(configuredAttributeSecret, signalText, StringComparison.Ordinal);
        Assert.DoesNotContain(rawIdentifier, triggerPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(crafted, triggerPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(configuredAttributeSecret, triggerPayload, StringComparison.Ordinal);
        var modelText = string.Join("\n", requests.SelectMany(request => request.Messages).Select(message => message.Content));
        Assert.NotEmpty(requests);
        Assert.DoesNotContain(rawIdentifier, modelText, StringComparison.Ordinal);
        Assert.DoesNotContain(crafted, modelText, StringComparison.Ordinal);
        Assert.DoesNotContain(configuredAttributeSecret, modelText, StringComparison.Ordinal);
        var pseudonymizer = scope.Factory.Services.GetRequiredService<UserIdentifierPseudonymizer>();
        Assert.True(pseudonymizer.IsCanonicalPseudonym(JsonValue.Create(canonicalUserId), "user.id"));
    }
    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_UnverifiableQuoteIsDroppedButCitationPersists()
    {
        using var scope = await CreateScopeAsync(postgres, services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient, InvalidQuoteModelClient>();
        });
        var ingested = await PostIngestAsync(scope.Client, "quote-drop");

        await RunClaimedJobAsync(scope, ingested.JobId!.Value, "worker-quote", maxAttempts: 1);

        var row = await ReadSingleEvidenceAsync(scope.ConnectionString, ingested.FaultId);
        Assert.Equal("TriggerSignal", row.Kind);
        Assert.Null(row.Quote);
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_VerifiableQuoteIsKept()
    {
        const string quote = "KEEP quote from trigger payload";
        using var scope = await CreateScopeAsync(postgres, services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient>(_ => new ValidQuoteModelClient(quote));
        });
        var unique = Guid.NewGuid().ToString("N");
        var ingested = await PostIngestAsync(scope.Client, new TriageReportTesterEnvelope(
            "tester",
            "quote-keep-svc-" + unique,
            "prod",
            DateTimeOffset.UtcNow,
            new TriageReportTesterAttributes("TimeoutException", quote, "/report-grounding")));
        Assert.NotNull(ingested.JobId);

        await RunClaimedJobAsync(scope, ingested.JobId.Value, "worker-quote-keep", maxAttempts: 1);

        var row = await ReadSingleEvidenceAsync(scope.ConnectionString, ingested.FaultId);
        Assert.Equal("TriggerSignal", row.Kind);
        Assert.Equal(quote, row.Quote);
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_FinalCommitFailureLeavesNoReportOrPublishedEvent()
    {
        using var scope = await CreateScopeAsync(postgres, services =>
        {
            services.RemoveAll<ITriageReportFinalCommitFaultInjector>();
            services.AddScoped<ITriageReportFinalCommitFaultInjector, ThrowBeforeReportPublished>();
        });
        var ingested = await PostIngestAsync(scope.Client, "final-rollback");

        await RunClaimedJobAsync(scope, ingested.JobId!.Value, "worker-final-failure", maxAttempts: 3);

        var jobStatus = await ScalarAsync<string>(scope.ConnectionString, "SELECT status FROM incidentcompass.triage_jobs WHERE id = @job_id;", ("job_id", ingested.JobId.Value));
        var reports = await ScalarAsync<long>(scope.ConnectionString, "SELECT COUNT(*) FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;", ("fault_id", ingested.FaultId));
        var published = await ScalarAsync<long>(scope.ConnectionString, "SELECT COUNT(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'ReportPublished';", ("job_id", ingested.JobId.Value));
        var delegated = await ScalarAsync<long>(scope.ConnectionString, "SELECT COUNT(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'Delegated';", ("job_id", ingested.JobId.Value));

        Assert.Equal("RetryPending", jobStatus);
        Assert.True(delegated > 0);
        Assert.Equal(0, reports);
        Assert.Equal(0, published);
    }

    private sealed class CapturingMockAiModelClient(ConcurrentQueue<AiModelRequest> requests) : IAiModelClient
    {
        private readonly MockAiModelClient inner = new();

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            requests.Enqueue(request);
            return inner.CompleteAsync(request, cancellationToken);
        }
    }
    private sealed class WorkerOutputThenTriggerEvidenceModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            if (!IsOrchestrator(request))
            {
                return Task.FromResult(Response(request, WorkerJson(), []));
            }

            if (!request.Messages.Any(static message => message.Role == AiMessageRole.Tool))
            {
                return Task.FromResult(Response(request, "delegate", [ToolCall("delegate-analysis", "delegate", "{\"role\":\"analysis\",\"task\":\"Analyze.\"}")]));
            }

            if (request.Messages.Any(static message => message.Content.Contains("publish_report_validation_failed", StringComparison.Ordinal)))
            {
                return Task.FromResult(Response(request, "publish valid", [PublishCall(request, FindPromptArtifactId(request, "TriggerSignal"), null)]));
            }

            var workerOutputId = FindToolResultArtifactId(request);
            return Task.FromResult(Response(request, "publish invalid", [PublishCall(request, workerOutputId, null)]));
        }
    }

    private sealed class ValidQuoteModelClient(string quote) : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            var referenceId = FindPromptArtifactId(request, "TriggerSignal");
            return Task.FromResult(Response(request, "publish", [PublishCall(request, referenceId, quote)]));
        }
    }

    private sealed class InvalidQuoteModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            var referenceId = FindPromptArtifactId(request, "TriggerSignal");
            return Task.FromResult(Response(request, "publish", [PublishCall(request, referenceId, "fabricated quote not present in artifact")]));
        }
    }

    private sealed class ThrowBeforeReportPublished : ITriageReportFinalCommitFaultInjector
    {
        public Task BeforeReportPublishedLedgerEventAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Injected final transaction failure before ReportPublished.");
        }
    }

    private static AiToolCall PublishCall(AiModelRequest request, string referenceId, string? quote)
    {
        var quoteJson = quote is null ? string.Empty : ",\"quote\":\"" + quote + "\"";
        return ToolCall("publish-" + Guid.NewGuid().ToString("N"), "publish_report", "{\"report_json\":{\"status\":\"Completed\",\"summary\":\"Grounded report.\",\"classification\":\"SimpleKnownError\",\"confidence\":\"Medium\",\"documentationFit\":\"Missing\",\"evidence\":[{\"referenceId\":\"" + referenceId + "\"" + quoteJson + "}],\"limitations\":[],\"recommendedNextAction\":\"Review the evidence.\"}}");
    }

    private static bool IsOrchestrator(AiModelRequest request)
    {
        var toolNames = request.Tools?.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        return toolNames.SetEquals(["delegate", "publish_report"]);
    }

    private static string WorkerJson()
    {
        return JsonSerializer.Serialize(new
        {
            keyFacts = NonCitableAnalysisKeyFacts,
            candidateClassification = "SimpleKnownError",
            needsDeeperContext = false,
            rationale = "Analysis completed."
        });
    }

    private static string FindPromptArtifactId(AiModelRequest request, string kind)
    {
        var prompt = request.Messages.First(static message => message.Role == AiMessageRole.User).Content;
        foreach (var line in prompt.Split('\n'))
        {
            if (line.Contains("kind=" + kind, StringComparison.Ordinal))
            {
                var marker = "artifact:";
                var start = line.IndexOf(marker, StringComparison.Ordinal);
                return line[(start + marker.Length)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            }
        }

        throw new InvalidOperationException("Prompt did not contain artifact kind " + kind + ".");
    }

    private static string FindToolResultArtifactId(AiModelRequest request)
    {
        var toolResult = request.Messages.Last(static message => message.Role == AiMessageRole.Tool).Content;
        using var document = JsonDocument.Parse(toolResult);
        return document.RootElement.GetProperty("artifactId").GetString()!;
    }

    private static AiModelResponse Response(AiModelRequest request, string content, IReadOnlyList<AiToolCall> toolCalls)
    {
        return new AiModelResponse(content, request.Model, "report-grounding-test", new AiModelUsage(10, 5, 15), request.CorrelationId, toolCalls);
    }

    private static AiToolCall ToolCall(string id, string name, string argumentsJson)
    {
        using var arguments = JsonDocument.Parse(argumentsJson);
        return new AiToolCall(id, name, "v1", arguments.RootElement.Clone());
    }
}
