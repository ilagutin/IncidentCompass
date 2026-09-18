namespace IncidentCompass.Infrastructure.Relevance;

/// <summary>
/// Which relevance judge the Worker composes. Bound from <c>IncidentCompass:RelevanceJudge</c>.
/// <c>LocalOnnx</c>, the default, is the in-process cross-encoder configured under
/// <c>IncidentCompass:RelevanceJudge:LocalOnnx</c>. <c>Mock</c> is a deterministic stand-in for the
/// mock stack, where every other model is mocked too, and needs no model directory.
/// </summary>
internal sealed class RelevanceJudgeOptions
{
    public const string SectionName = "IncidentCompass:RelevanceJudge";

    public const string ProviderKey = SectionName + ":Provider";

    public const string LocalOnnxProvider = "LocalOnnx";

    public const string MockProvider = "Mock";

    public string Provider { get; init; } = LocalOnnxProvider;
}
