using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Application;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Get;
using IncidentCompass.Application.Governance.ActionApprovals.List;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.Tickets;
using IncidentCompass.Worker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// One database, one configuration and one monitored checkout, composed the way a single-host
/// deployment composes them: an Api host for the two things a person does (send a signal, approve an
/// action) and a Worker-composed provider for everything the backend does.
/// </summary>
/// <remarks>
/// <para>
/// The two hosts are separate providers over one database on purpose. The chain only exists in a host
/// that registers the post-report workflows and the external-action adapters, which the Api host
/// deliberately does not; and an Api host that also registered them would start the pumps, which would
/// make every assertion in the chain test a statement about which of two timers fired first. Here the
/// pumps are driven call by call, so the walk is a sequence, not a race.
/// </para>
/// <para>
/// Nothing leaves the process: the model is scripted, the ticket search is a port stub, and every
/// GitHub call reaches <see cref="RemediationChainGitHub" />.
/// </para>
/// </remarks>
internal sealed class RemediationChainWorld : IAsyncDisposable
{
    public const string ServiceName = "checkout";
    public const string Release = "2026.09.11.1";
    public const string TenantId = "local";
    public const string Owner = "acme";
    public const string Repository = "checkout";
    public const string RepositorySlug = Owner + "/" + Repository;
    public const string BaseBranch = "main";
    public const string SourcePath = "src/Checkout.cs";
    public const string BaseSourceText = "namespace Checkout;\n";
    public const string PatchedSourceText = "namespace Checkout.Fixed;\n";
    public const int ExistingIssueNumber = 42;

    public const string PatchText =
        "--- a/src/Checkout.cs\n+++ b/src/Checkout.cs\n@@ -1 +1 @@\n" +
        "-namespace Checkout;\n+namespace Checkout.Fixed;\n";

    private const string OperatorKey = "chain_operator_key_abcdefghijklmnopqrstuvwxyz12";

    private readonly WebApplicationFactory<Program> factory;
    private readonly string temporaryRoot;

    private RemediationChainWorld(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        ServiceProvider worker,
        RemediationChainGitHub gitHub,
        ScriptedRemediationModelClient model,
        string connectionString,
        string temporaryRoot)
    {
        this.factory = factory;
        this.temporaryRoot = temporaryRoot;
        Client = client;
        Worker = worker;
        GitHub = gitHub;
        Model = model;
        ConnectionString = connectionString;
    }

    public HttpClient Client { get; }

    public ServiceProvider Worker { get; }

    public RemediationChainGitHub GitHub { get; }

    public ScriptedRemediationModelClient Model { get; }

    public string ConnectionString { get; }

    public string MonitoredRoot => Path.Combine(temporaryRoot, "monitored");

    public static async Task<RemediationChainWorld> CreateAsync(
        string connectionString,
        string patchAnswer)
    {
        var temporaryRoot = Directory.CreateTempSubdirectory("ic-chain-").FullName;
        var monitoredRoot = Path.Combine(temporaryRoot, "monitored");
        var sourceFile = Path.Combine(monitoredRoot, "src", "Checkout.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        await File.WriteAllTextAsync(sourceFile, BaseSourceText, TestContext.Current.CancellationToken);

        var gitHub = new RemediationChainGitHub(Owner, Repository);
        gitHub.AddBaseFile(SourcePath, System.Text.Encoding.UTF8.GetBytes(BaseSourceText));
        gitHub.AddIssue(ExistingIssueNumber);
        var model = new ScriptedRemediationModelClient(patchAnswer);
        var settings = Settings(connectionString, temporaryRoot);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var setting in settings)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Add("X-IncidentCompass-Key", OperatorKey);
        return new RemediationChainWorld(
            factory,
            client,
            BuildWorker(settings, gitHub, model),
            gitHub,
            model,
            connectionString,
            temporaryRoot);
    }

    /// <summary>
    /// Sends one signal through the real intake endpoint and returns the fault and job it created.
    /// </summary>
    public async Task<(Guid FaultId, Guid JobId)> IngestAsync(string errorMessage)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/incidents",
            new
            {
                sourceKind = "otel",
                serviceName = ServiceName,
                environment = "prod",
                externalId = "chain-" + Guid.NewGuid().ToString("N"),
                observedAtUtc = DateTimeOffset.UtcNow,
                attributes = new Dictionary<string, object?>
                {
                    ["errorType"] = "CheckoutTotalException",
                    ["errorMessage"] = errorMessage,
                    ["exception.stacktrace"] =
                        "   at Checkout.Total() in " +
                        Path.Combine(MonitoredRoot, "src", "Checkout.cs") + ":line 1"
                }
            },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var ingested = await response.Content.ReadFromJsonAsync<IngestedSignal>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(ingested);
        Assert.NotNull(ingested.JobId);
        return (ingested.FaultId, ingested.JobId.Value);
    }

    /// <summary>
    /// Runs the investigation the way the Worker runs it, with the two governed reads this chain needs
    /// and the report that cites both. Publication is the real publisher, so it is the real
    /// transaction that writes the post-report intents.
    /// </summary>
    public async Task<Guid> InvestigateAndPublishAsync(Guid jobId, string reportSummary)
    {
        using var scope = Worker.CreateScope();
        var services = scope.ServiceProvider;
        var job = await services.GetRequiredService<ITriageJobRunner>().ClaimNextAsync(
            "chain-worker", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.NotNull(job);
        Assert.Equal(jobId, job.Id);
        var configuration = await services.GetRequiredService<ITriageConfigurationRepository>()
            .GetByHashAsync(job.ConfigHash, TestContext.Current.CancellationToken);
        var investigation = await services.GetRequiredService<ITriageJobInvestigationContextRepository>()
            .GetAsync(job.Id, job.Attempt, TestContext.Current.CancellationToken);
        var executor = services.GetRequiredService<WorkerToolCallExecutor>();

        var sourceArtifactId = await ToolArtifactAsync(
            executor, job, configuration, investigation, "source", "source_lookup");
        var ticketArtifactId = await ToolArtifactAsync(
            executor, job, configuration, investigation, "tickets", "ticket_search");
        await services.GetRequiredService<TriageReportPublisher>().PublishAsync(
            job,
            "chain-worker",
            new AiToolCall(
                "publish-chain",
                "publish_report",
                "v1",
                JsonSerializer.SerializeToElement(new
                {
                    report_json = new
                    {
                        status = "Completed",
                        summary = reportSummary,
                        classification = "SimpleKnownError",
                        confidence = "Medium",
                        documentationFit = "Missing",
                        evidence = new[]
                        {
                            new { referenceId = sourceArtifactId, quote = "namespace Checkout;" },
                            new { referenceId = ticketArtifactId, quote = "Checkout totals are off by one" }
                        },
                        limitations = Array.Empty<string>(),
                        recommendedNextAction = "Review the cited source and the cited ticket."
                    }
                })),
            TestContext.Current.CancellationToken);
        return await ReadReportIdAsync(job.Id);
    }

    /// <summary>
    /// Drives the post-report evaluation loop to a standstill: every claimable intent is evaluated,
    /// and the call returns only when there is nothing left to claim and nothing still running.
    /// </summary>
    public async Task DrainWorkflowsAsync()
    {
        var pump = Worker.GetRequiredService<PostReportActionEvaluationPump>();
        var options = new PostReportActionEvaluationOptions
        {
            LeaseSeconds = 120,
            MaximumAttempts = 3,
            FirstRetryDelaySeconds = 1,
            SecondRetryDelaySeconds = 1,
            ScanBatchSize = 8,
            PollIntervalSeconds = 1,
            MaxConcurrency = 4
        };
        while (true)
        {
            var claimed = await pump.FillAvailableSlotsAsync(
                "chain-evaluation", options, TestContext.Current.CancellationToken);
            while (pump.ActiveEvaluationCount > 0)
            {
                await pump.WaitForNextWakeAsync(
                    TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
                await pump.ObserveCompletedAsync(TestContext.Current.CancellationToken);
            }

            if (claimed == 0)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Approves one requested action the way an operator does: read the proposal back over the API,
    /// and echo the two digests it showed. A stale or edited digest is refused by the same call.
    /// </summary>
    public async Task<Guid> ApproveAsync(Guid faultId, string toolId)
    {
        var action = await FindAsync(faultId, toolId);
        Assert.Equal(ActionApprovalState.Requested.ToStorageValue(), action.Status);
        var details = await ReadDetailsAsync(action.Id);
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/action-approvals/{action.Id}/approve",
            new { payloadSha256 = details.PayloadSha256, approvalSha256 = details.ApprovalSha256 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = await response.Content.ReadFromJsonAsync<ActionApprovalDetailsResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(approved);
        Assert.Equal(ActionApprovalState.Approved.ToStorageValue(), approved.Status);
        return action.Id;
    }

    public async Task<HttpStatusCode> TryApproveWithAsync(
        Guid actionId,
        string payloadSha256,
        string approvalSha256)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/action-approvals/{actionId}/approve",
            new { payloadSha256, approvalSha256 },
            TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    /// <summary>Claims and dispatches one approved action, the way the action pump does.</summary>
    public async Task DispatchAsync(Guid actionId, string workerId)
    {
        using var scope = Worker.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>();
        var claim = await dispatcher.TryClaimAsync(
            actionId, workerId, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotNull(claim);
        await dispatcher.DispatchAsync(
            claim, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    /// <summary>Whether a second dispatch of the same action can even be claimed.</summary>
    public async Task<bool> CanClaimAsync(Guid actionId, string workerId)
    {
        using var scope = Worker.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>()
            .TryClaimAsync(
                actionId, workerId, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
            is not null;
    }

    public async Task<ActionApprovalListItemResponse> FindAsync(Guid faultId, string toolId)
    {
        var listed = await ListAsync(faultId);
        return Assert.Single(listed, item => string.Equals(item.ToolId, toolId, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<ActionApprovalListItemResponse>> ListAsync(Guid faultId)
    {
        var response = await Client.GetFromJsonAsync<ActionApprovalListResponse>(
            $"/api/v1/action-approvals?faultId={faultId}&limit=50",
            TestContext.Current.CancellationToken);
        Assert.NotNull(response);
        return response.Actions;
    }

    public async Task<ActionApprovalDetailsResponse> ReadDetailsAsync(Guid actionId)
    {
        var details = await Client.GetFromJsonAsync<ActionApprovalDetailsResponse>(
            "/api/v1/action-approvals/" + actionId, TestContext.Current.CancellationToken);
        Assert.NotNull(details);
        return details;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        factory.Dispose();
        await Worker.DisposeAsync();
        try
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    private static async Task<string> ToolArtifactAsync(
        WorkerToolCallExecutor executor,
        TriageJob job,
        TriageConfiguration configuration,
        TriageJobInvestigationContext investigation,
        string role,
        string toolName)
    {
        var output = await executor.ExecuteAsync(
            job,
            configuration,
            investigation,
            role,
            new AiToolCall(toolName + "-call", toolName, "v1", EmptyArguments()),
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(output);
        return Assert.Single(document.RootElement.GetProperty("items").EnumerateArray())
            .GetProperty("artifactId").GetString()!;
    }

    private static JsonElement EmptyArguments()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private async Task<Guid> ReadReportIdAsync(Guid jobId)
    {
        var value = await ActionApprovalTestSupport.ScalarAsync(
            ConnectionString,
            "SELECT id FROM incidentcompass.triage_reports WHERE job_id = @job_id;",
            ("job_id", jobId));
        return Assert.IsType<Guid>(value);
    }

    private static Dictionary<string, string> Settings(string connectionString, string temporaryRoot) =>
        new(StringComparer.Ordinal)
        {
            ["ConnectionStrings:IncidentCompass"] = connectionString,
            ["IncidentCompass:ModelGateway:Provider"] = "Mock",
            ["IncidentCompass:Embeddings:Provider"] = "Mock",
            ["IncidentCompass:Memory:Seed:Enabled"] = "false",
            ["IncidentCompass:Telegram:Enabled"] = "false",
            ["IncidentCompass:ConfigSource:Path"] = Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "remediation-chain-triage-config",
                "incidentcompass.config.json"),
            ["IncidentCompass:SourceContext:Roots:0:ServiceName"] = ServiceName,
            ["IncidentCompass:SourceContext:Roots:0:Release"] = Release,
            ["IncidentCompass:SourceContext:Roots:0:RootPath"] = Path.Combine(temporaryRoot, "monitored"),
            ["IncidentCompass:SourceContext:WorkspaceRoot"] = Path.Combine(temporaryRoot, "workspaces"),
            ["IncidentCompass:Tickets:GitHub:Owner"] = Owner,
            ["IncidentCompass:Tickets:GitHub:Repository"] = Repository,
            ["IncidentCompass:Tickets:GitHub:Token"] = "chain-token-sentinel",
            ["IncidentCompass:Publication:GitHub:BaseBranch"] = BaseBranch,
            ["IncidentCompass:ApiKeyAuth:Enabled"] = "true",
            ["IncidentCompass:ApiKeyAuth:PermitLimit"] = "1000",
            ["IncidentCompass:ApiKeyAuth:WindowSeconds"] = "300",
            ["IncidentCompass:ApiKeyAuth:Credentials:0:KeyId"] = "chain-operator",
            ["IncidentCompass:ApiKeyAuth:Credentials:0:TenantId"] = TenantId,
            ["IncidentCompass:ApiKeyAuth:Credentials:0:Sha256Digest"] = Digest(OperatorKey)
        };

    private static string Digest(string key) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// The Worker host's own composition, with the three boundaries a deterministic run replaces: the
    /// model, the ticket-search port, and every GitHub adapter's transport.
    /// </summary>
    private static ServiceProvider BuildWorker(
        IReadOnlyDictionary<string, string> settings,
        RemediationChainGitHub gitHub,
        ScriptedRemediationModelClient model)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(
                setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication(configuration);
        services.AddInfrastructure(configuration);
        services.AddWorker(configuration);

        services.RemoveAll<IAiModelClient>();
        services.AddScoped<IAiModelClient>(_ => model);
        services.Replace(ServiceDescriptor.Scoped<ITicketSearch>(_ => new MatchedTicketSearch()));
        services.Replace(ServiceDescriptor.Singleton(provider => new GitHubCodePublicationGateway(
            provider.GetRequiredService<IOptions<GitHubIssuesOptions>>(),
            provider.GetRequiredService<IOptions<GitHubCodePublicationOptions>>(),
            gitHub)));
        services.Replace(ServiceDescriptor.Scoped(provider => new GitHubIssueCommentExternalActionTool(
            provider.GetRequiredService<IOptions<GitHubIssuesOptions>>(), gitHub)));
        services.Replace(ServiceDescriptor.Scoped(provider => new GitHubIssueBacklinkExternalActionTool(
            provider.GetRequiredService<IOptions<GitHubIssuesOptions>>(), gitHub)));
        services.Replace(ServiceDescriptor.Scoped(provider => new GitHubIssuesTicketCreate(
            provider.GetRequiredService<IOptions<GitHubIssuesOptions>>(),
            provider.GetRequiredService<ITicketActionHistory>(),
            gitHub)));
        return services.BuildServiceProvider();
    }

    private sealed record IngestedSignal(Guid FaultId, Guid? JobId);

    /// <summary>
    /// The one existing ticket this chain's report cites. It is a port stub rather than an HTTP
    /// answer because ticket search is a read whose provider shape another test already covers, and
    /// what this chain needs from it is one cited issue in the configured repository.
    /// </summary>
    private sealed class MatchedTicketSearch : ITicketSearch
    {
        public Task<TicketSearchResult> SearchAsync(
            TicketSearchRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TicketSearchResult(
                TicketSearchOutcome.Matched,
                "ticket_search_matches",
                [
                    new TicketSearchMatch(
                        "github",
                        RepositorySlug,
                        ExistingIssueNumber.ToString(CultureInfo.InvariantCulture),
                        "Checkout totals are off by one",
                        "open",
                        "octocat",
                        new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
                        $"https://github.com/{RepositorySlug}/issues/{ExistingIssueNumber}",
                        0.91)
                ]));
    }
}
