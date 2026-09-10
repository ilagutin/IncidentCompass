using IncidentCompass.Application;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Worker;

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
