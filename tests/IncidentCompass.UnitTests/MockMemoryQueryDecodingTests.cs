using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Memory;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.ModelGateway.Mock;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The mock memory worker reads its query out of the rendered prompt, and the prompt writes every
/// untrusted scalar as a JSON string literal. A real model decodes that literal on the way into its
/// tool arguments, so the mock has to decode it too. A letter of any Basic Multilingual Plane script
/// now arrives as itself, but the surrounding quotes are always there and the quote, the backslash,
/// the control characters, the separators and the format characters are still escaped, so reading the
/// raw line would carry the quotes and Latin <c>u0022</c> fragments into the query. Those are counted
/// words in the corpus script, which both fails lexical coverage and hides the query's real script
/// from the foreign-script fallback; the decode is what keeps that from happening.
/// </summary>
public sealed class MockMemoryQueryDecodingTests
{
    private static readonly Guid FaultId = Guid.Parse("40000000-0000-0000-0000-000000000001");

    private const string RussianErrorMessage = "Таймаут оформления заказа при вызове платежного сервиса";

    [Fact]
    public void CreateSearchArguments_EnglishPromptCarriesTheWordsWithoutTheJsonQuotes()
    {
        var query = ReadQuery(WorkerPrompt(
            "payments-api",
            "Checkout failed while calling inventory",
            "TimeoutException",
            "Checkout timed out while calling inventory"));

        Assert.Equal("payments-api TimeoutException Checkout timed out while calling inventory", query);
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

    /// <summary>
    /// The decode is still load-bearing now that letters arrive readable: a quote inside the message
    /// is written as an escape, and reading the line raw would carry <c>u0022</c> into the query.
    /// </summary>
    [Fact]
    public void CreateSearchArguments_AnEmbeddedQuoteDecodesBesideTheReadableCyrillic()
    {
        const string messageWithQuote = "\"pay\" " + RussianErrorMessage;

        var query = ReadQuery(WorkerPrompt(
            "payments-api",
            "Checkout failed while calling inventory",
            "TimeoutException",
            messageWithQuote));

        Assert.Contains(messageWithQuote, query, StringComparison.Ordinal);
        Assert.DoesNotContain("u0022", query, StringComparison.Ordinal);
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
            "- summary: \"Checkout failed\"",
            "- errorType: not a json literal",
            "- errorMessage: \"unterminated \\q\"");

        var query = ReadQuery(prompt);

        Assert.Equal(
            "\"payments-api not a json literal \"unterminated \\q\"",
            query);
    }

    /// <summary>
    /// The band is decided against the backend's fault query, and the mock role's query is meant to be
    /// that same query read back out of the prompt, which is also what the memory role instructions
    /// tell a real role to search with. This pins the two together field by field: each field carries a
    /// distinct marker, so a mock that read a different field, dropped one or reordered them would
    /// produce a different string. A blank error type is skipped by both, and a blank message is
    /// replaced by the summary in both.
    /// </summary>
    [Theory]
    [InlineData("TimeoutException", "message marker words", "service-marker TimeoutException message marker words")]
    [InlineData(null, "message marker words", "service-marker message marker words")]
    [InlineData("TimeoutException", null, "service-marker TimeoutException summary marker words")]
    public void CreateSearchArguments_TheMockQueryIsTheFaultQueryOfTheSameSignal(
        string? errorType,
        string? errorMessage,
        string expected)
    {
        var signal = TriggerSignal("service-marker", "summary marker words", errorType, errorMessage);

        var query = ReadQuery(WorkerPrompt(signal));

        Assert.Equal(MemoryFaultQuery.For(signal), query);
        Assert.Equal(expected, query);
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
    private static string WorkerPrompt(string serviceName, string summary, string errorType, string errorMessage) =>
        WorkerPrompt(TriggerSignal(serviceName, summary, errorType, errorMessage));

    private static Signal TriggerSignal(string serviceName, string summary, string? errorType, string? errorMessage)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new Signal(
            Guid.Parse("40000000-0000-0000-0000-000000000003"), "tenant", "tester", FaultId, "fingerprint", 1,
            FingerprintStrength.Strong, true, null, false, null, null, null, null, null,
            serviceName, "production", null, "Error", errorType, errorMessage, summary,
            null, null, null, null, null, EmptyObject(), EmptyObject(), now, now, null);
    }

    /// <summary>
    /// The fault carries the signal's own service name, as ingestion gives it: the prompt's
    /// <c>service</c> line is the fault's, and the fault query reads the signal's.
    /// </summary>
    private static string WorkerPrompt(Signal signal)
    {
        var now = DateTimeOffset.UnixEpoch;
        var job = new TriageJob(
            Guid.Parse("40000000-0000-0000-0000-000000000002"), FaultId, TriageJobStatus.Processing, 1,
            "worker-test", now.AddMinutes(5), null, null, null, "config-hash", now, now);
        var fault = new Fault(
            FaultId, signal.Id, "tenant", FaultStatus.Analyzing, "fingerprint", 1,
            FingerprintStrength.Strong, true, signal.ServiceName, "production", "Error", null, now, null, null);

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
