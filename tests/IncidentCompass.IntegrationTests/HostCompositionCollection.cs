namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The host-composition tests were a single class until they outgrew the test-scope file limit.
/// xUnit parallelizes across collections rather than within one, so splitting the class into three
/// would have started running the three parts against each other; naming one collection for all of
/// them keeps the scheduling the single class had.
/// </summary>
[CollectionDefinition(CollectionName)]
public sealed class HostCompositionCollection
{
    public const string CollectionName = "host composition";
}
