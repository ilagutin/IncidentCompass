using IncidentCompass.Application;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Configuration;

namespace IncidentCompass.Worker;

/// <summary>
/// The Worker host entry point. It is a named class in this namespace rather than top-level
/// statements because top-level statements compile to a type called <c>Program</c> in the global
/// namespace: the Api host already publishes one, and a test project that sees the internals of
/// both hosts would have two global <c>Program</c> types to choose from.
/// </summary>
internal static class WorkerHostEntryPoint
{
    private static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddApplication(builder.Configuration);
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddPostgresMigrations();
        builder.Services.AddWorker(builder.Configuration);

        var host = builder.Build();
        var validationExitCode = await TriageConfigurationValidationCommand.RunIfRequestedAsync(args, host.Services);
        if (validationExitCode.HasValue)
        {
            Environment.ExitCode = validationExitCode.Value;
            return;
        }

        host.Run();
    }
}
