using IncidentCompass.Api.Security;
using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Governance.ActionApprovals.Approve;
using IncidentCompass.Application.Governance.ActionApprovals.Get;
using IncidentCompass.Application.Governance.ActionApprovals.List;
using IncidentCompass.Application.Governance.ActionApprovals.Reject;

namespace IncidentCompass.Api;

internal static class ActionApprovalEndpoints
{
    public static RouteGroupBuilder MapActionApprovalEndpoints(this RouteGroupBuilder api)
    {
        var approvals = api.MapGroup("/action-approvals")
            .WithTags("action-approvals")
            .RequireAuthorization(ActionOperatorAuthorizationPolicy.Name);

        approvals.MapGet(string.Empty, ListAsync)
            .WithName("ListActionApprovals")
            .WithSummary("List tenant-scoped action approvals by paired external resource or by fault.")
            .Produces<ActionApprovalListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests);

        approvals.MapGet("/{id:guid}", GetAsync)
            .WithName("GetActionApproval")
            .WithSummary("Return the immutable action proposal tuple and grounded provenance for review.")
            .Produces<ActionApprovalDetailsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests);

        approvals.MapPost("/{id:guid}/approve", ApproveAsync)
            .WithName("ApproveAction")
            .WithSummary("Approve the exact observed action payload and approval hashes.")
            .Produces<ActionApprovalDetailsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests);

        approvals.MapPost("/{id:guid}/reject", RejectAsync)
            .WithName("RejectAction")
            .WithSummary("Reject the exact observed action payload and approval hashes.")
            .Produces<ActionApprovalDetailsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests);

        return api;
    }

    private static async Task<IResult> ListAsync(
        string? status,
        int? limit,
        string? cursor,
        string? externalResourceKind,
        string? externalResourceId,
        Guid? faultId,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.DispatchAsync<ListActionApprovalsQuery, ActionApprovalListResponse>(
            new ListActionApprovalsQuery(
                status,
                limit,
                cursor,
                externalResourceKind,
                externalResourceId,
                faultId),
            cancellationToken));

    private static async Task<IResult> GetAsync(
        Guid id,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.DispatchAsync<GetActionApprovalQuery, ActionApprovalDetailsResponse>(
            new GetActionApprovalQuery(id), cancellationToken));

    private static async Task<IResult> ApproveAsync(
        Guid id,
        ApproveActionRequest request,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.DispatchAsync<ApproveActionCommand, ActionApprovalDetailsResponse>(
            new ApproveActionCommand(id, request.PayloadSha256, request.ApprovalSha256), cancellationToken));

    private static async Task<IResult> RejectAsync(
        Guid id,
        RejectActionRequest request,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.DispatchAsync<RejectActionCommand, ActionApprovalDetailsResponse>(
            new RejectActionCommand(id, request.PayloadSha256, request.ApprovalSha256, request.Reason), cancellationToken));
}
