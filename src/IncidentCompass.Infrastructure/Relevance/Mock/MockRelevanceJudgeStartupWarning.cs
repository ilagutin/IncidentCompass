using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.Infrastructure.Relevance.Mock;

/// <summary>
/// Says, once at Worker start, that this host confirms memory matches with the mock relevance judge.
/// </summary>
/// <remarks>
/// A mock confirmation is indistinguishable from a real one in the tool output and in the stored
/// artifact, so the log is the one place an operator reading a host can see which judge produced it.
/// It is a Warning rather than Information because the mock is the right judge for the mock stack and
/// the wrong one anywhere else, and on a production host it would be the first sign of that. It carries
/// no query, candidate or score.
/// </remarks>
internal sealed partial class MockRelevanceJudgeStartupWarning(
    ILogger<MockRelevanceJudgeStartupWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LogMockJudgeComposed(logger, RelevanceJudgeOptions.ProviderKey);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        2323,
        LogLevel.Warning,
        "This Worker runs the mock relevance judge ({Setting} is Mock). It is a deterministic stand-in that" +
        " confirms memory matches by matching error-type names, not a governance boundary, and must never" +
        " run on a production host.")]
    private static partial void LogMockJudgeComposed(ILogger logger, string setting);
}
