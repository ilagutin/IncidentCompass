namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Reads the number of the one pull request a report's governed chain actually opened.
/// </summary>
/// <remarks>
/// <para>
/// <b>It reads the compact audit projection, not a result document.</b> The number comes from the
/// <c>external_resource_id</c> column of the executed, live <c>pr_create</c> row, which a check
/// constraint already restricts to a positive integer and which the terminal transaction wrote under
/// the same validation that decided the action succeeded. "Confirmed" therefore means a value the
/// database will vouch for, not a field a reader hoped to find in JSON, which is exactly what that
/// projection was added for.
/// </para>
/// <para>
/// <b>Nothing else leaves.</b> Not a URL, not a title, not a branch. A number is all a backlink needs
/// and all this port will give it, so nothing a provider echoed can reach a ticket through here.
/// </para>
/// </remarks>
public interface IConfirmedPullRequestReader
{
    /// <summary>
    /// The pull-request number for one report, or <see langword="null" /> when no executed, live
    /// pull-request action recorded one, or when more than one did and durable state therefore names
    /// no single pull request.
    /// </summary>
    Task<string?> FindConfirmedPullRequestNumberAsync(
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken);
}
