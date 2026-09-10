namespace IncidentCompass.Worker;

internal sealed class ActionDispatchOptions
{
    public const string SectionName = "IncidentCompass:ActionDispatch";

    public int BatchSize { get; set; } = 8;

    public int PollIntervalSeconds { get; set; } = 5;

    public int AdapterTimeoutSeconds { get; set; } = 30;
}
