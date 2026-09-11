using IncidentCompass.Application.Core.ModelClients;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The only model in the chain walk: it answers the remediation pass with one scripted patch and
/// records the prompts it was shown.
/// </summary>
/// <remarks>
/// <para>
/// Recording the prompts is not incidental. The injection case has to show that hostile text reached
/// the model - otherwise it proves nothing about the path a real incident takes - and that everything
/// the text asked for was decided elsewhere. So the prompt is kept, asserted to contain the
/// instruction, and the assertions about repository, branch, base, commit and approval are made
/// against what the backend wrote instead.
/// </para>
/// <para>
/// It offers no tool calls at all, which is what the remediation pass asks for: the pass calls with
/// <c>tools: null</c>, so there is no surface here for a model to name an action.
/// </para>
/// </remarks>
internal sealed class ScriptedRemediationModelClient(string answer) : IAiModelClient
{
    private readonly List<string> prompts = [];

    public IReadOnlyList<string> Prompts => prompts;

    public int Calls => prompts.Count;

    public Task<AiModelResponse> CompleteAsync(
        AiModelRequest request,
        CancellationToken cancellationToken)
    {
        Assert.Null(request.Tools);
        lock (prompts)
        {
            prompts.Add(string.Join(
                "\n",
                request.Messages.Select(static message => message.Role + ": " + message.Content)));
        }

        return Task.FromResult(new AiModelResponse(
            answer,
            request.Model,
            "remediation-chain-script",
            new AiModelUsage(64, 32, 96),
            request.CorrelationId));
    }
}
