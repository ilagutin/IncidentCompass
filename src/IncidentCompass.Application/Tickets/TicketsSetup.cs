using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IncidentCompass.Application.Tickets;

internal static class TicketsSetup
{
    public static IServiceCollection AddTicketsCore(this IServiceCollection services)
    {
        services.TryAddScoped<ITicketSearch, UnavailableTicketSearch>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IImmediateAgentTool, TicketSearchTool>());
        services.AddSingleton(new AgentToolDescriptor("ticket_search", AgentToolCapability.ImmediateRead));
        services.AddSingleton(TicketCreateTool.Descriptor);
        services.AddSingleton(TicketUpdatePostReportActionWorkflow.Descriptor);

        // The backlink is a second comment id on the same target, registered here for the same reason
        // as the first: a configuration naming it has to validate on every host that loads one, not
        // only on the host that can dispatch it.
        services.AddSingleton(TicketBacklinkDescriptor.Descriptor);
        return services;
    }
}
