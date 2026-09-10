using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Tickets;
using IncidentCompass.TestSupport;
using IncidentCompass.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Which configurations a host will and will not start on. Every test here drives one real host
/// through <c>StartAsync</c> and reads the options-validation failures back out of the exception,
/// so an option that stops being validated, or a bound value that stops reaching the component that
/// uses it, shows up as a host that starts when it should not.
/// </summary>
[Collection(HostCompositionCollection.CollectionName)]
public sealed class HostOptionsValidationTests
{
    public static IEnumerable<object[]> InvalidApplicationConfigurations =>
    [
        [new Dictionary<string, string?> { ["IncidentCompass:Application:ApiVersion"] = " " }]
    ];

    public static IEnumerable<object[]> InvalidModelGatewayConfigurations =>
    [
        [new Dictionary<string, string?> { ["IncidentCompass:ModelGateway:Provider"] = " " }],
        [new Dictionary<string, string?> { ["IncidentCompass:ModelGateway:DefaultTemperature"] = "1.5" }],
        [
            new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:MinTemperature"] = "0.8",
                ["IncidentCompass:ModelGateway:MaxTemperature"] = "0.7"
            }
        ],
        [new Dictionary<string, string?> { ["IncidentCompass:ModelGateway:DefaultMaxOutputTokens"] = "0" }],
        [
            new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:DefaultMaxOutputTokens"] = "4096",
                ["IncidentCompass:ModelGateway:MaxOutputTokensLimit"] = "2048"
            }
        ],
        [new Dictionary<string, string?> { ["IncidentCompass:ModelGateway:MaxInputMessageCharacters"] = "0" }],
        [new Dictionary<string, string?> { ["IncidentCompass:ModelGateway:MaxCorrelationIdLength"] = "129" }]
    ];

    public static IEnumerable<object[]> AcceptedProviderSpellings =>
    [
        ["Mock"],
        ["OpenAiCompatible"],
        ["OPENAI_COMPATIBLE"],
        ["OPENAI-COMPATIBLE"]
    ];

    [Fact]
    public async Task HostServices_RejectUnsupportedModelGatewayProviderOnStart()
    {
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IncidentCompass:ModelGateway:Provider"] = "TypoProvider"
                });
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
            })
            .Build();

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("unsupported", StringComparison.OrdinalIgnoreCase) &&
                       failure.Contains("TypoProvider", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AcceptedProviderSpellings))]
    public async Task HostServices_AcceptsDocumentedProviderSpellings(string provider)
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:ModelGateway:Provider"] = provider,
            ["IncidentCompass:ModelGateway:OpenAiCompatible:ApiKey"] = "test-api-key",
            ["IncidentCompass:Embeddings:Provider"] = provider,
            ["IncidentCompass:Embeddings:OpenAiCompatible:ApiKey"] = "test-api-key"
        });

        await host.StartAsync();

        using var scope = host.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAiModelClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IEmbeddingClient>());
    }

    [Theory]
    [MemberData(nameof(InvalidApplicationConfigurations))]
    public async Task HostServices_RejectInvalidApplicationOptionsOnStart(
        IReadOnlyDictionary<string, string?> invalidConfiguration)
    {
        using var host = CreateHostWithConfiguration(invalidConfiguration);

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        // [OptionsValidator] (source-generated) emits one failure per failing property, formatted
        // by the attribute's ErrorMessage. The invalidated field name appears in the failure
        // text, which is what we anchor the assertion on now.
        var expectedFieldName = invalidConfiguration.Keys.First().Split(':')[^1];
        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains(expectedFieldName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HostServices_RejectInvalidEmbeddingOptionsOnStart()
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:Embeddings:MockDimensions"] = "0"
        });

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        // [OptionsValidator] (source-generated) emits one failure per failing property; the
        // invalidated field name appears in the failure text.
        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("MockDimensions", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(InvalidModelGatewayConfigurations))]
    public async Task HostServices_RejectInvalidModelGatewayOptionsOnStart(
        IReadOnlyDictionary<string, string?> values)
    {
        using var host = CreateHostWithConfiguration(values);

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("Model gateway configuration", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("1023", "1")]
    [InlineData("1048577", "1")]
    [InlineData("1024", "0")]
    [InlineData("1024", "1025")]
    public async Task HostServices_RejectInvalidIngestionLimitsOnStart(string payloadBytes, string attributesBytes)
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:IngestionLimits:MaxPayloadBytes"] = payloadBytes,
            ["IncidentCompass:IngestionLimits:MaxAttributesBytes"] = attributesBytes
        });

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.NotEmpty(GetOptionsValidationFailures(exception));
    }

    /// <summary>
    /// Both retention operations are destructive and irreversible, so an out-of-bounds window has to
    /// stop the host rather than be clamped or ignored at the first run. A zero-day window is the
    /// case that matters: it is what an empty or mistyped setting would otherwise become, and it
    /// means "empty the payload the moment it lands".
    /// </summary>
    [Theory]
    [InlineData("IncidentCompass:Retention:SignalPayloadRetentionDays", "0")]
    [InlineData("IncidentCompass:Retention:SignalPayloadRetentionDays", "3651")]
    [InlineData("IncidentCompass:Retention:AttemptArtifactRetentionDays", "0")]
    [InlineData("IncidentCompass:Retention:AttemptArtifactRetentionDays", "-1")]
    [InlineData("IncidentCompass:Retention:MaxRowsPerRun", "0")]
    [InlineData("IncidentCompass:Retention:MaxRowsPerRun", "100001")]
    public async Task HostServices_RejectInvalidRetentionOptionsOnStart(string key, string value)
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?> { [key] = value });

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        var expectedFieldName = key.Split(':')[^1];
        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains(expectedFieldName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The schedule is bound by the Worker rather than by the shared Application composition, so it
    /// needs a Worker host to be validated at all. An interval outside the bounds has to stop the
    /// host for the same reason the windows do: retention deletes rows, and a zero or negative
    /// interval - what an empty or mistyped setting binds to - means running it in a tight loop.
    /// </summary>
    [Theory]
    [InlineData("IncidentCompass:RetentionSchedule:IntervalMinutes", "0")]
    [InlineData("IncidentCompass:RetentionSchedule:IntervalMinutes", "-1")]
    [InlineData("IncidentCompass:RetentionSchedule:IntervalMinutes", "1441")]
    public async Task WorkerHostServices_RejectInvalidRetentionScheduleOnStart(string key, string value)
    {
        using var host = CreateWorkerHostWithConfiguration(new Dictionary<string, string?> { [key] = value });

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        var expectedFieldName = key.Split(':')[^1];
        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains(expectedFieldName, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1", "1", "1")]
    [InlineData("3650", "3650", "100000")]
    public async Task HostServices_AcceptsRetentionOptionsAtInclusiveBounds(
        string signalDays,
        string artifactDays,
        string maxRows)
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:Retention:SignalPayloadRetentionDays"] = signalDays,
            ["IncidentCompass:Retention:AttemptArtifactRetentionDays"] = artifactDays,
            ["IncidentCompass:Retention:MaxRowsPerRun"] = maxRows
        });

        await host.StartAsync();
    }

    [Theory]
    [InlineData("1024", "1")]
    [InlineData("1048576", "1048576")]
    public async Task HostServices_AcceptsIngestionLimitsAtInclusiveBounds(string payloadBytes, string attributesBytes)
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:IngestionLimits:MaxPayloadBytes"] = payloadBytes,
            ["IncidentCompass:IngestionLimits:MaxAttributesBytes"] = attributesBytes
        });

        await host.StartAsync();
    }

    [Fact]
    public async Task MockEmbeddingClient_UsesConfiguredDimensions()
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:Embeddings:MockDimensions"] = "1024"
        });
        await host.StartAsync();

        var embeddingClient = host.Services.GetRequiredService<IEmbeddingClient>();
        var response = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest("dimension test", "mock-embedding", CorrelationId: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(1024, response.Vector.Count);
    }

    [Fact]
    public async Task HostServices_ValidateAndComposeGitHubTicketSearchWithoutARealCall()
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
            ["IncidentCompass:Tickets:GitHub:Repository"] = "repo",
            ["IncidentCompass:Tickets:GitHub:Token"] = "host-secret"
        });

        await host.StartAsync();
        using var scope = host.Services.CreateScope();

        Assert.IsType<GitHubIssuesTicketSearch>(scope.ServiceProvider.GetRequiredService<ITicketSearch>());
    }

    [Fact]
    public async Task HostServices_RejectInvalidGitHubRepositoryIdentityOnStart()
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["IncidentCompass:Tickets:GitHub:Owner"] = "owner",
            ["IncidentCompass:Tickets:GitHub:Repository"] = "other/repo"
        });

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains(GetOptionsValidationFailures(exception),
            failure => failure.Contains("Repository", StringComparison.Ordinal));
    }

    private static IHost CreateHostWithConfiguration(
        IReadOnlyDictionary<string, string?> values)
    {
        // These hosts exercise ModelGateway/Embeddings option validation only, but AddInfrastructure
        // now also registers the intake infrastructure, whose warmup hosted service needs a
        // real triage config file to resolve at StartAsync -- point it at the repo's checked-in
        // config so these unrelated tests do not need to know about intake at all.
        var configurationOverrides = new Dictionary<string, string?>(values)
        {
            ["IncidentCompass:ConfigSource:Path"] = Path.Combine(RepositoryRootLocator.Find(), "config", "incidentcompass.config.json")
        };

        return new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(configurationOverrides);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
            })
            .Build();
    }

    /// <summary>
    /// The same host as <see cref="CreateHostWithConfiguration" /> plus <c>AddWorker</c>, for options
    /// only the Worker binds. It is used for the rejecting direction, where <c>StartAsync</c> fails on
    /// options validation before any hosted service starts, so nothing here reaches a database.
    /// </summary>
    private static IHost CreateWorkerHostWithConfiguration(
        IReadOnlyDictionary<string, string?> values)
    {
        var configurationOverrides = new Dictionary<string, string?>(values)
        {
            ["IncidentCompass:ConfigSource:Path"] =
                Path.Combine(RepositoryRootLocator.Find(), "config", "incidentcompass.config.json")
        };

        return new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(configurationOverrides);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
                services.AddWorker(context.Configuration);
            })
            .Build();
    }

    private static IEnumerable<string> GetOptionsValidationFailures(Exception exception)
    {
        if (exception is OptionsValidationException optionsValidationException)
        {
            return optionsValidationException.Failures;
        }

        if (exception is AggregateException aggregateException)
        {
            return aggregateException
                .Flatten()
                .InnerExceptions
                .OfType<OptionsValidationException>()
                .SelectMany(static optionsValidationException => optionsValidationException.Failures);
        }

        return [];
    }
}
