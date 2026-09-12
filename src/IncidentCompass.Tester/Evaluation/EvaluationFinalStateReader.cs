using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace IncidentCompass.Tester.Evaluation;

internal sealed class EvaluationFinalStateReader(HttpClient client, TimeSpan recoveryTimeout)
{
    public async Task<EvaluationObservedState?> ReadAsync(
        IngestSignalResponse ingested,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(recoveryTimeout);
        var fault = await TryGetAsync(
            "api/v1/faults/" + ingested.FaultId,
            TesterJsonContext.Default.FaultDetailsResponse,
            timeout.Token);
        cancellationToken.ThrowIfCancellationRequested();
        var readableLedger = await TryGetAsync(
            "api/v1/faults/" + ingested.FaultId + "/ledger",
            TesterJsonContext.Default.FaultLedgerResponse,
            timeout.Token);
        cancellationToken.ThrowIfCancellationRequested();
        if (fault is null && readableLedger is null)
        {
            return null;
        }

        var ledger = readableLedger ?? new FaultLedgerResponse(ingested.FaultId, []);
        var expectedJobMatched = ingested.JobId.HasValue &&
            fault?.Job?.Id == ingested.JobId.Value;
        var reportId = expectedJobMatched
            ? TryReadReportId(ledger, fault!.Job!.Id, fault.Job.Attempt)
            : null;
        var report = reportId.HasValue
            ? await TryGetAsync(
                "api/v1/triage-reports/" + reportId.Value,
                TesterJsonContext.Default.TriageReportResponse,
                timeout.Token)
            : null;
        cancellationToken.ThrowIfCancellationRequested();
        return new EvaluationObservedState(
            fault,
            ledger,
            report,
            LedgerObservationAvailable: readableLedger is not null,
            ExpectedJobMatched: expectedJobMatched);
    }

    public static Guid? TryReadReportId(FaultLedgerResponse ledger, Guid jobId, int attempt)
    {
        var value = ledger.Events.LastOrDefault(item =>
            item.EventType == "ReportPublished" &&
            item.JobId == jobId &&
            item.Attempt == attempt)?.PayloadRef;
        return value is not null && value.StartsWith("report:", StringComparison.Ordinal) && Guid.TryParse(value[7..], out var id)
            ? id
            : null;
    }

    private async Task<T?> TryGetAsync<T>(
        string path,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await client.GetFromJsonAsync(path, jsonTypeInfo, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
