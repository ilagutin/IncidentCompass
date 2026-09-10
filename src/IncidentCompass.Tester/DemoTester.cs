using System.Globalization;
using System.Net.Http.Json;

namespace IncidentCompass.Tester;

internal sealed class DemoTester(HttpClient client, TesterOptions options)
{
    internal const int ActionObservationReadCount = 4;
    private readonly TransientHttpRetry httpRetry = new(options.PollInterval);
    public Task<int> RunAsync(CancellationToken cancellationToken) =>
        DemoDeadlineRunner.RunTotalAsync(
            options.TotalTimeout,
            RunWithinTotalDeadlineAsync,
            cancellationToken);
    private async Task<int> RunWithinTotalDeadlineAsync(CancellationToken cancellationToken)
    {
        await EnsureHealthyAsync(cancellationToken);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var emission = await OtlpDemoEmitter.EmitFailureAsync(ResolveOtlpTracesEndpoint(options.BaseUrl), runId, cancellationToken);
        Console.WriteLine($"OTLP SDK scenario exported error span {emission.TraceId}/{emission.SpanId} for {emission.ServiceName}.");
        var scenarios = DemoScenario.CreateAll(runId)
            .Append(DemoInjectionScenarioLoader.Load())
            .ToArray();
        return await RunScenariosAsync(scenarios, runId, cancellationToken);
    }

    internal async Task<int> RunScenariosAsync(
        IReadOnlyList<DemoScenario> scenarios,
        string runId,
        CancellationToken cancellationToken)
    {
        var results = new List<DemoResult>(scenarios.Count);
        foreach (var scenario in scenarios)
        {
            try
            {
                results.Add(await DemoDeadlineRunner.RunScenarioAsync(
                    scenario,
                    options.ScenarioTimeout,
                    token => RunScenarioAsync(scenario, runId, token),
                    cancellationToken));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                results.Add(CreateFailedResult(scenario, exception.Message));
            }
        }
        DemoResultPrinter.Print(results);
        return results.All(static result => result.Passed) ? 0 : 1;
    }

    private async Task EnsureHealthyAsync(CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        var deadline = DateTimeOffset.UtcNow.Add(options.PollTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync("api/v1/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
                lastFailure = new HttpRequestException("Health endpoint returned " + (int)response.StatusCode + ".");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastFailure = exception;
            }
            await Task.Delay(options.PollInterval, cancellationToken);
        }
        throw new InvalidOperationException("API health check did not succeed before the tester timeout.", lastFailure);
    }

    private async Task<DemoResult> RunScenarioAsync(
        DemoScenario scenario,
        string runId,
        CancellationToken cancellationToken)
    {
        var ingested = new List<IngestSignalResponse>();
        for (var index = 1; index <= scenario.SignalCount; index++)
        {
            ingested.Add(await PostIncidentAsync(scenario.CreateEnvelope(runId, index), cancellationToken));
        }
        var target = ingested.FirstOrDefault(static item => item.JobId is not null) ?? ingested[^1];
        var ledgerUrl = BuildUrl($"api/v1/faults/{target.FaultId}/ledger");
        var reportId = await WaitForReportIdAsync(target.FaultId, cancellationToken);
        if (reportId is null)
        {
            return new DemoResult(scenario, target.FaultId, null, null, null, ledgerUrl, null, false, "report not published");
        }
        var reportUrl = BuildUrl($"api/v1/triage-reports/{reportId}");
        var report = await GetReportAsync(reportId.Value, cancellationToken);
        var passed = DemoExpectationChecker.Matches(scenario, report, out var detail);
        if (scenario.RequiresNoActionGate)
        {
            var actionGate = await DemoActionGateResult.ObserveAsync(
                ActionObservationReadCount,
                options.PollInterval,
                GetLedgerAsync,
                target.FaultId,
                cancellationToken);
            passed &= actionGate.Passed;
            detail = string.Equals(detail, "ok", StringComparison.Ordinal)
                ? actionGate.Detail
                : detail + "; " + actionGate.Detail;
        }
        return new DemoResult(
            scenario,
            target.FaultId,
            report.Id,
            report.Classification,
            report.IsMassIssue,
            ledgerUrl,
            reportUrl,
            passed,
            detail);
    }

    private async Task<IngestSignalResponse> PostIncidentAsync(
        IncidentEnvelope envelope,
        CancellationToken cancellationToken)
    {
        return await httpRetry.ExecuteAsync(async token =>
        {
            using var response = await client.PostAsJsonAsync(
                "api/v1/incidents",
                envelope,
                TesterJsonContext.Default.IncidentEnvelope,
                token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync(
                TesterJsonContext.Default.IngestSignalResponse,
                token) ?? throw new InvalidOperationException("Incident response body was empty.");
        }, cancellationToken);
    }

    private async Task<Guid?> WaitForReportIdAsync(Guid faultId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(options.PollTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ledger = await GetLedgerAsync(faultId, cancellationToken);
            var reportRef = ledger.Events
                .LastOrDefault(static item => string.Equals(item.EventType, "ReportPublished", StringComparison.Ordinal))
                ?.PayloadRef;
            if (TryParseReportId(reportRef, out var reportId))
            {
                return reportId;
            }
            await Task.Delay(options.PollInterval, cancellationToken);
        }
        return null;
    }

    private Task<FaultLedgerResponse> GetLedgerAsync(Guid faultId, CancellationToken cancellationToken) =>
        httpRetry.ExecuteAsync(async token =>
            await client.GetFromJsonAsync(
                $"api/v1/faults/{faultId}/ledger",
                TesterJsonContext.Default.FaultLedgerResponse,
                token) ?? throw new InvalidOperationException("Ledger response body was empty."),
            cancellationToken);
    private Task<TriageReportResponse> GetReportAsync(Guid reportId, CancellationToken cancellationToken) =>
        httpRetry.ExecuteAsync(async token =>
            await client.GetFromJsonAsync(
                $"api/v1/triage-reports/{reportId}",
                TesterJsonContext.Default.TriageReportResponse,
                token) ?? throw new InvalidOperationException("Report response body was empty."),
            cancellationToken);
    private static DemoResult CreateFailedResult(DemoScenario scenario, string detail) =>
        new(scenario, null, null, null, null, null, null, false, detail);

    private static Uri ResolveOtlpTracesEndpoint(Uri apiBaseUrl)
    {
        var configured = Environment.GetEnvironmentVariable("INCIDENTCOMPASS_TESTER_OTLP_TRACES_ENDPOINT");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new Uri(apiBaseUrl, "v1/traces");
        }
        var endpoint = new Uri(configured);
        if (!endpoint.IsAbsoluteUri || !endpoint.AbsolutePath.EndsWith("/v1/traces", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("INCIDENTCOMPASS_TESTER_OTLP_TRACES_ENDPOINT must be an absolute /v1/traces URL.");
        }
        return endpoint;
    }

    private string BuildUrl(string path) => new Uri(options.PublicBaseUrl, path).ToString();

    private static bool TryParseReportId(string? payloadRef, out Guid reportId)
    {
        reportId = Guid.Empty;
        const string prefix = "report:";
        return payloadRef is not null &&
            payloadRef.StartsWith(prefix, StringComparison.Ordinal) &&
            Guid.TryParse(payloadRef[prefix.Length..], out reportId);
    }
}
