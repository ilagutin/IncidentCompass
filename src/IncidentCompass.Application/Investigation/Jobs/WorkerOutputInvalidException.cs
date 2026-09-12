namespace IncidentCompass.Application.Investigation.Jobs;

internal sealed class WorkerOutputInvalidException : Exception
{
    internal const string ErrorCode = "worker_output_invalid";
    internal const string StoredReason =
        "worker_output_invalid: worker output remained invalid after bounded reprompts.";

    public WorkerOutputInvalidException(WorkerOutputValidationException innerException)
        : base("Worker output remained invalid after the configured reprompt limit.", innerException)
    {
    }
}
