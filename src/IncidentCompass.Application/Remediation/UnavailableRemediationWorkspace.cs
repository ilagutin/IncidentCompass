namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The default a host gets when nothing wired a monitored checkout: remediation is not configured,
/// and both calls say so.
/// </summary>
/// <remarks>
/// It exists so that the absence of an adapter is an outcome rather than a missing registration. A
/// pass reaching an unconfigured host gets the same closed code it would get for an unmapped service
/// and stops; it does not fail to resolve a dependency at the moment a job is being processed.
/// </remarks>
internal sealed class UnavailableRemediationWorkspace : IRemediationWorkspace
{
    public Task<RemediationBaseResult> IdentifyBaseAsync(
        RemediationTarget target,
        CancellationToken cancellationToken) =>
        Task.FromResult(RemediationBaseResult.Refused(RemediationCodes.NotConfigured));

    public Task<RemediationApplyResult> ApplyAsync(
        RemediationApplyRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(RemediationApplyResult.Refused(
            RemediationCodes.NotConfigured,
            answerCorrectable: false));
}
