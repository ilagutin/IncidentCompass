namespace IncidentCompass.Worker;

internal sealed class WorkerOptions
{
    public const string SectionName = "IncidentCompass:Worker";

    public int MaxConcurrentJobs { get; set; } = 1;

    public int PollIntervalSeconds { get; set; } = 30;

    public int LeaseSeconds { get; set; } = 900;

    public int MaxAttempts { get; set; } = 3;

    public int RetryDelaySeconds { get; set; } = 10;
}
