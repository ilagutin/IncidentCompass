namespace IncidentCompass.Worker;

internal sealed class PostReportActionEvaluationOptions
{
    public const string SectionName = "IncidentCompass:PostReportActions";

    public int LeaseSeconds { get; set; } = 30;

    public int MaximumAttempts { get; set; } = 3;

    public int FirstRetryDelaySeconds { get; set; } = 5;

    public int SecondRetryDelaySeconds { get; set; } = 10;

    public int ScanBatchSize { get; set; } = 8;

    public int PollIntervalSeconds { get; set; } = 5;

    public int MaxConcurrency { get; set; } = 4;
}
