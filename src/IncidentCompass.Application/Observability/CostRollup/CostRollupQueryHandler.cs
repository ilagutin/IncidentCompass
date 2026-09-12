using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Exceptions;
using IncidentCompass.Application.Core.Security;

namespace IncidentCompass.Application.Observability.CostRollup;

public sealed class CostRollupQueryHandler(
    IModelCostRollupRepository repository,
    IUserContext userContext) : IRequestHandler<CostRollupQuery, CostRollupResponse>
{
    public async Task<CostRollupResponse> HandleAsync(
        CostRollupQuery request,
        CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated || string.IsNullOrWhiteSpace(userContext.TenantId))
        {
            throw new ForbiddenRequestException(
                "Authenticated tenant context is required.",
                ApplicationErrorCodes.TenantContextRequired,
                "Authentication did not resolve a tenant for this request.");
        }

        var hours = await repository.ReadAsync(
            userContext.TenantId,
            request.FromUtc,
            request.ToUtc,
            cancellationToken);
        return new CostRollupResponse(
            request.FromUtc,
            request.ToUtc,
            hours,
            CostRollupResponse.EmbeddingCallsExcludedNotice);
    }
}
