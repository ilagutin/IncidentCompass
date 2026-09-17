using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.ModelGateway.Mock;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The mock memory worker reads its query out of the rendered prompt, and the prompt writes every
/// scalar as a JSON string literal. A real model decodes that literal on the way into its tool
/// arguments, so the mock has to decode it too; reading the raw text turned a Cyrillic error message
/// into Latin <c>u0422</c> fragments, which are counted words in the corpus script and therefore both
/// failed lexical coverage and hid the query's real script from the foreign-script fallback.
/// </summary>
public sealed class MockMemoryQueryDecodingTests
{
    private const string RussianErrorMessage = "Таймаут оформления заказа при вызове платежного сервиса";

    [Fact]
    public void CreateSearchArguments_EnglishPromptCarriesTheWordsWithoutTheJsonQuotes()
    {
        var query = ReadQuery(WorkerPrompt(
            "payments-api",
            "Checkout failed while calling inventory",
            "TimeoutException",
            "Checkout timed out while calling inventory"));

        Assert.Equal(
            "payments-api Checkout failed while calling inventory TimeoutException " +
            "Checkout timed out while calling inventory",
            query);
        Assert.DoesNotContain('"', query);
    }

    [Fact]
    public void CreateSearchArguments_CyrillicMessageKeepsItsOwnScriptAndNoEscapeFragments()
    {
        var query = ReadQuery(WorkerPrompt(
            "payments-api",
            "Checkout failed while calling inventory",
            "TimeoutException",
            RussianErrorMessage));

        Assert.Contains(RussianErrorMessage, query, StringComparison.Ordinal);
        Assert.DoesNotContain("u04", query, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", query, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSearchArguments_MalformedValueFallsBackToTheRawText()
    {
        var prompt = string.Join(
            '\n',
            "IncidentCompass memory worker task.",
            "Fault:",
            "- service: \"payments-api",
            "Trigger signal:",
            "- summary: not a json literal",
            "- errorType: \"TimeoutException\"",
            "- errorMessage: \"unterminated \\q\"");

        var query = ReadQuery(prompt);

        Assert.Equal(
            "\"payments-api not a json literal TimeoutException \"unterminated \\q\"",
            query);
    }

    [Fact]
    public void CreateSearchArguments_PromptWithNoValuesFallsBackToTheFixedQuery()
    {
        Assert.Equal("incident memory", ReadQuery("IncidentCompass memory worker task."));
    }

    private static string ReadQuery(string prompt)
    {
        var request = new AiModelRequest(
            "mock-memory-query",
            "mock-model",
            [new AiChatMessage(AiMessageRole.User, prompt)]);

        using var document = JsonDocument.Parse(MockIncidentCompassMemoryQuery.CreateSearchArguments(request));
        return document.RootElement.GetProperty("query").GetString()!;
    }

    /// <summary>
    /// Renders the prompt through the production builder, so the test shares the exact encoding the
    /// mock has to read back rather than a hand-written approximation of it.
    /// </summary>
    private static string WorkerPrompt(string serviceName, string summary, string errorType, string errorMessage)
    {
        var now = DateTimeOffset.UnixEpoch;
        var faultId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var job = new TriageJob(
            Guid.Parse("40000000-0000-0000-0000-000000000002"), faultId, TriageJobStatus.Processing, 1,
            "worker-test", now.AddMinutes(5), null, null, null, "config-hash", now, now);
        var signal = new Signal(
            Guid.Parse("40000000-0000-0000-0000-000000000003"), "tenant", "tester", faultId, "fingerprint", 1,
            FingerprintStrength.Strong, true, null, false, null, null, null, null, null,
            serviceName, "production", null, "Error", errorType, errorMessage, summary,
            null, null, null, null, null, EmptyObject(), EmptyObject(), now, now, null);
        var fault = new Fault(
            faultId, signal.Id, "tenant", FaultStatus.Analyzing, "fingerprint", 1,
            FingerprintStrength.Strong, true, serviceName, "production", "Error", null, now, null, null);

        return TriageInvestigationPromptBuilder.BuildWorkerPrompt(
            "memory",
            "Search incident memory.",
            job,
            new TriageJobInvestigationContext(fault, signal, []));
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
