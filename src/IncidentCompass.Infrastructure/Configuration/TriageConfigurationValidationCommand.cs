using IncidentCompass.Infrastructure.Intake;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Configuration;

public static class TriageConfigurationValidationCommand
{
    public static async Task<int?> RunIfRequestedAsync(
        string[] args,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        if (args.Length != 2 ||
            !string.Equals(args[0], "config", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(args[1], "validate", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            await using var scope = services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<FileTriageConfigurationRepository>();
            await repository.ValidateCurrentAsync(cancellationToken);

            var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
            var source = scope.ServiceProvider.GetRequiredService<IOptions<TriageConfigSourceOptions>>().Value;
            var path = Path.GetFullPath(Path.Combine(environment.ContentRootPath, source.Path));
            Console.WriteLine($"Triage configuration is valid: {path}");
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            Console.Error.WriteLine("Triage configuration is invalid: " + exception.Message);
            return 1;
        }
    }
}
