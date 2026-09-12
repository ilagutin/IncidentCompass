using IncidentCompass.Api;
using IncidentCompass.Application;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Memory;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddPostgresMigrations();
builder.Services.AddApi(builder.Configuration, builder.Environment);

var app = builder.Build();

var validationExitCode = await TriageConfigurationValidationCommand.RunIfRequestedAsync(args, app.Services);
if (validationExitCode.HasValue)
{
    Environment.ExitCode = validationExitCode.Value;
    return;
}

var memoryExitCode = await MemoryCorpusCommand.RunIfRequestedAsync(args, app.Services);
if (memoryExitCode.HasValue)
{
    Environment.ExitCode = memoryExitCode.Value;
    return;
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous().DisableRateLimiting();
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHealthChecks("/health").AllowAnonymous().DisableRateLimiting();
app.MapApiV1();
app.MapOtlpEndpoints();

app.Run();

public partial class Program;
