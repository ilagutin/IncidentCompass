using IncidentCompass.Application.Core.ModelGateway;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

/// <summary>
/// Names what ended an HTTP attempt with a cancellation the caller did not request.
/// </summary>
/// <remarks>
/// <para>
/// The phase decides which limit can be responsible. After the response started only the inactivity
/// limit is in play, so a first-output timer that happens to fire while the body is read is never
/// reported. Before it started, the first-output timer is recognised by its own token having fired.
/// </para>
/// <para>
/// The connect limit cannot be recognised by a token, because it lives on the socket handler. When
/// <c>ConnectTimeout</c> elapses the handler raises an <see cref="OperationCanceledException" /> whose
/// direct inner exception is a <see cref="TimeoutException" />, and <see cref="HttpClient" /> passes it
/// through unchanged. The only other producer of that exact shape on this path is
/// <see cref="HttpClient" /> itself, when its own <see cref="HttpClient.Timeout" /> elapses, so the
/// shape is accepted as a connect timeout only while that client timeout is infinite, as the
/// registration sets it. Any other cancellation that is not attributable to a limit is reported as a
/// dispatch whose outcome is unknown, never as a timeout it may not have been.
/// </para>
/// </remarks>
internal static class OpenAiAttemptCancellationClassifier
{
    public static AiModelException Map(
        OperationCanceledException exception,
        bool responseStarted,
        bool firstOutputTimerFired,
        bool inactivityTimerFired,
        bool clientHasOwnTimeout)
    {
        if (responseStarted)
        {
            return inactivityTimerFired
                ? OpenAiModelErrorMapper.StreamInactivityTimeout(exception)
                : OpenAiModelErrorMapper.DispatchOutcomeUnknown(exception);
        }

        if (firstOutputTimerFired)
        {
            return OpenAiModelErrorMapper.FirstOutputTimeout(exception);
        }

        return !clientHasOwnTimeout && exception.InnerException is TimeoutException
            ? OpenAiModelErrorMapper.ConnectTimeout(exception)
            : OpenAiModelErrorMapper.DispatchOutcomeUnknown(exception);
    }
}
