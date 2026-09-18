using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Memory;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The fault query is built by the backend from the trigger signal, and the band of every returned
/// memory document is decided against it. Its shape is pinned here: the service name, the error type
/// and the error message, in that order, blank parts skipped, the summary only in place of a blank
/// message, and the same bound the role's own query is held to.
/// </summary>
public sealed class MemoryFaultQueryTests
{
    [Fact]
    public void For_JoinsServiceErrorTypeAndMessageInThatOrderWithOneSpace()
    {
        var signal = MemorySearchToolTestSupport.TriggerSignal(
            "summary words",
            "ErrorTypeMarker",
            "message words",
            serviceName: "service-marker");

        Assert.Equal("service-marker ErrorTypeMarker message words", MemoryFaultQuery.For(signal));
    }

    /// <summary>
    /// The summary intake synthesizes for a structured signal carries a templated "failed" frame that
    /// pulled unrelated faults towards every incident document about the service, so it is left out
    /// whenever the signal has a message of its own.
    /// </summary>
    [Fact]
    public void For_LeavesTheSummaryOutWhenTheSignalHasAMessage()
    {
        var signal = MemorySearchToolTestSupport.TriggerSignal(
            "checkout-api: POST /checkout failed - TimeoutException: timed out",
            "TimeoutException",
            "timed out");

        Assert.Equal("checkout-api TimeoutException timed out", MemoryFaultQuery.For(signal));
    }

    /// <summary>
    /// A user or manual report has no message, and its summary is then the only description it has.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void For_UsesTheSummaryInPlaceOfABlankMessage(string? errorMessage)
    {
        var signal = MemorySearchToolTestSupport.TriggerSignal(
            "Checkout button does nothing",
            errorMessage: errorMessage);

        Assert.Equal("checkout-api Checkout button does nothing", MemoryFaultQuery.For(signal));
    }

    /// <summary>
    /// A structured signal with an error type and no message gets a summary intake synthesized itself,
    /// the templated frame the fault query exists to leave out. It is recognized by recomputing the
    /// synthesis, so the fault query is the service name and the error type only.
    /// </summary>
    [Fact]
    public void For_LeavesOutTheSummaryIntakeSynthesizedForAStructuredSignalWithNoMessage()
    {
        var synthesized = SummarySynthesizer.ForStructuredSignal("checkout-api", null, null, "TimeoutException", null);
        var signal = MemorySearchToolTestSupport.TriggerSignal(synthesized, "TimeoutException");

        Assert.Equal("checkout-api: operation failed - TimeoutException", synthesized);
        Assert.Equal("checkout-api TimeoutException", MemoryFaultQuery.For(signal));
    }

    /// <summary>A user report's summary is the reporter's own text, and is the description used.</summary>
    [Fact]
    public void For_UsesAUserReportsOwnSummary()
    {
        var signal = MemorySearchToolTestSupport.TriggerSignal("Site is down, checkout button does nothing");

        Assert.Equal("checkout-api Site is down, checkout button does nothing", MemoryFaultQuery.For(signal));
    }

    /// <summary>
    /// A structured signal whose sender supplied its own summary and no message keeps that summary:
    /// only the backend's frame is left out, never the sender's words.
    /// </summary>
    [Fact]
    public void For_UsesASenderSuppliedSummaryOnAStructuredSignalWithNoMessage()
    {
        var signal = MemorySearchToolTestSupport.TriggerSignal("Checkout stalls on stock reservation", "TimeoutException");

        Assert.Equal("checkout-api TimeoutException Checkout stalls on stock reservation", MemoryFaultQuery.For(signal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compose_SkipsABlankPartRatherThanLeavingTwoSpaces(string? errorType)
    {
        Assert.Equal(
            "service message",
            MemoryFaultQuery.Compose("service", errorType, "message", "summary"));
    }

    [Fact]
    public void For_NoSignalIsNoFaultQuery()
    {
        Assert.Null(MemoryFaultQuery.For(null));
    }

    /// <summary>
    /// The judge reads the fault query inside the same token window as the role's query, so it is held
    /// to the same bound, and the cut never splits a surrogate pair: here the pair straddles the bound
    /// exactly, and the cut moves one unit earlier instead of leaving half of it behind.
    /// </summary>
    [Fact]
    public void Compose_CutsToTheQueryBoundOnARuneBoundary()
    {
        var bound = MemorySearchQueryBound.MaxQueryCharacters;
        var pair = char.ConvertFromUtf32(0x1F600);
        const string prefix = "svc t ";
        var message = new string('a', bound - prefix.Length - 1) + pair + "tail";

        var query = MemoryFaultQuery.Compose("svc", "t", message, null);

        Assert.True(char.IsHighSurrogate(pair[0]));
        Assert.Equal(bound - 1, query.Length);
        Assert.False(char.IsSurrogate(query[^1]));
        Assert.StartsWith(prefix, query, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_AQueryWithinTheBoundIsNotCut()
    {
        var message = new string('b', 100);

        Assert.Equal("svc " + message, MemoryFaultQuery.Compose("svc", null, message, null));
    }
}
