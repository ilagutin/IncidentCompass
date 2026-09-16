using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The full truth table of what an unrequested cancellation of one HTTP attempt is reported as.
/// </summary>
public sealed class OpenAiAttemptCancellationClassifierTests
{
    private const string Unknown = "provider_dispatch_outcome_unknown";

    public static TheoryData<bool, bool, bool, bool, bool, bool, string> Cases => new()
    {
        // responseStarted, outputStarted, firstOutputFired, inactivityFired, clientTimeout, timeoutShape, expected
        { true, true, false, true, false, false, ProviderErrorCodes.StreamInactivityTimeout },
        { true, true, true, true, false, false, ProviderErrorCodes.StreamInactivityTimeout },
        { true, true, true, false, false, false, Unknown },
        { true, true, false, false, false, true, Unknown },
        { true, false, true, false, false, false, ProviderErrorCodes.FirstOutputTimeout },
        { false, false, true, false, false, true, ProviderErrorCodes.FirstOutputTimeout },
        { true, false, false, false, false, true, Unknown },
        { true, false, false, true, false, false, Unknown },
        { false, false, false, false, false, true, ProviderErrorCodes.ConnectTimeout },
        { false, false, false, false, true, true, Unknown },
        { false, false, false, false, false, false, Unknown }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Map_NamesOnlyTheLimitThePhaseAllows(
        bool responseStarted,
        bool outputStarted,
        bool firstOutputFired,
        bool inactivityFired,
        bool clientHasOwnTimeout,
        bool timeoutShape,
        string expected)
    {
        var cancellation = timeoutShape
            ? new TaskCanceledException("Timed out.", new TimeoutException("Timed out."))
            : new OperationCanceledException("Cancelled.");

        var exception = OpenAiAttemptCancellationClassifier.Map(
            cancellation,
            responseStarted,
            outputStarted,
            firstOutputFired,
            inactivityFired,
            clientHasOwnTimeout);

        Assert.Equal(expected, exception.ErrorCode);
        Assert.Same(cancellation, exception.InnerException);
    }
}
