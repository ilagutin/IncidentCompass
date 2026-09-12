using IncidentCompass.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

public sealed class PostgresStartupRetryOptionsTests
{
    [Fact]
    public void DefaultsAreWithinTheStartupBudgetContract()
    {
        var options = new PostgresStartupRetryOptions();

        Assert.Equal(10, options.MaxAttempts);
        Assert.Equal(250, options.InitialDelayMilliseconds);
        Assert.Equal(2000, options.MaxDelayMilliseconds);
        Assert.Equal(30, options.MaxTotalDurationSeconds);
        Assert.True(CreateValidator().Validate(null, new PostgresOptions()).Succeeded);
    }

    /// <summary>
    /// Two cases are worth spelling out. A zero initial delay passes every other bound and turns
    /// the wait into a loop that retries as fast as the operating system can refuse the port. An
    /// enormous expiry or attempt count passes every floor and turns a loud startup failure into a
    /// host that waits silently for hours, so the ceilings are as load-bearing as the floors.
    /// </summary>
    [Theory]
    [InlineData(0, 250, 2000, 30)]
    [InlineData(-1, 250, 2000, 30)]
    [InlineData(101, 250, 2000, 30)]
    [InlineData(10, 0, 2000, 30)]
    [InlineData(10, -1, 2000, 30)]
    [InlineData(10, 60001, 60001, 30)]
    [InlineData(10, 250, 249, 30)]
    [InlineData(10, 250, 60001, 30)]
    [InlineData(10, 250, 2000, 0)]
    [InlineData(10, 250, 2000, -1)]
    [InlineData(10, 250, 2000, 601)]
    public void InvalidBoundsFailStartup(
        int maxAttempts,
        int initialDelayMs,
        int maxDelayMs,
        int maxTotalDurationSeconds)
    {
        var result = CreateValidator().Validate(null, new PostgresOptions
        {
            StartupRetry = new PostgresStartupRetryOptions
            {
                MaxAttempts = maxAttempts,
                InitialDelayMilliseconds = initialDelayMs,
                MaxDelayMilliseconds = maxDelayMs,
                MaxTotalDurationSeconds = maxTotalDurationSeconds
            }
        });

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(100, 60000, 60000, 600)]
    public void InclusiveBoundsAreAccepted(
        int maxAttempts,
        int initialDelayMs,
        int maxDelayMs,
        int maxTotalDurationSeconds)
    {
        var result = CreateValidator().Validate(null, new PostgresOptions
        {
            StartupRetry = new PostgresStartupRetryOptions
            {
                MaxAttempts = maxAttempts,
                InitialDelayMilliseconds = initialDelayMs,
                MaxDelayMilliseconds = maxDelayMs,
                MaxTotalDurationSeconds = maxTotalDurationSeconds
            }
        });

        Assert.True(result.Succeeded);
    }

    private static IValidateOptions<PostgresOptions> CreateValidator()
    {
        var type = typeof(PostgresOptions).Assembly.GetType(
            "IncidentCompass.Infrastructure.Configuration.PostgresStartupRetryOptionsValidator")
            ?? throw new InvalidOperationException(
                "PostgresStartupRetryOptionsValidator type was not found.");
        return (IValidateOptions<PostgresOptions>)Activator.CreateInstance(type, nonPublic: true)!;
    }
}
