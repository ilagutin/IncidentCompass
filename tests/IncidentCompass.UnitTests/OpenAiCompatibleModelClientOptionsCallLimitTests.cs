using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The host options for the provider call limits: their defaults, the backward-compatible reading
/// of the deprecated <c>TimeoutSeconds</c>, the rejection of both spellings at once, the permitted
/// ranges, the connect limit reaching the socket handler, and the start-up warning for the old key.
/// </summary>
public sealed class OpenAiCompatibleModelClientOptionsCallLimitTests
{
    private const int DeprecatedTimeoutWarningEventId = 2801;

    [Fact]
    public void Defaults_AreTheMaintainerLimits()
    {
        var options = ValidOptions();

        Assert.Equal(30, options.ConnectTimeoutSeconds);
        Assert.Equal(600, options.ResolveFirstOutputTimeoutSeconds());
        Assert.Equal(600, options.StreamInactivityTimeoutSeconds);
        Assert.False(options.UsesDeprecatedTimeoutSeconds);
        Assert.True(options.IsValid());
    }

    [Fact]
    public void DeprecatedTimeoutSeconds_AloneIsTheFirstOutputLimit()
    {
        var options = ValidOptions(timeoutSeconds: 300);

        Assert.True(options.UsesDeprecatedTimeoutSeconds);
        Assert.Equal(300, options.ResolveFirstOutputTimeoutSeconds());
        Assert.True(options.IsValid());
    }

    [Fact]
    public void NewAndDeprecatedFirstOutputKeys_Together_AreInvalid()
    {
        var options = ValidOptions(firstOutputTimeoutSeconds: 600, timeoutSeconds: 300);

        Assert.True(options.HasConflictingFirstOutputTimeouts);
        Assert.False(options.IsTransportValid());
        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData(0, 600, 600, false)]
    [InlineData(601, 600, 600, false)]
    [InlineData(600, 600, 600, true)]
    [InlineData(30, 0, 600, false)]
    [InlineData(30, 3601, 600, false)]
    [InlineData(30, 3600, 600, true)]
    [InlineData(30, 600, 0, false)]
    [InlineData(30, 600, 3601, false)]
    [InlineData(30, 600, 3600, true)]
    public void CallLimits_AreValidatedWithinTheirRanges(
        int connectTimeoutSeconds,
        int firstOutputTimeoutSeconds,
        int streamInactivityTimeoutSeconds,
        bool expectedValid)
    {
        var options = ValidOptions(
            connectTimeoutSeconds: connectTimeoutSeconds,
            firstOutputTimeoutSeconds: firstOutputTimeoutSeconds,
            streamInactivityTimeoutSeconds: streamInactivityTimeoutSeconds);

        Assert.Equal(expectedValid, options.IsValid());
    }

    [Fact]
    public void AddInfrastructure_BindsTheNewKeysAndGivesTheSocketHandlerTheConnectLimit()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["IncidentCompass:ModelGateway:OpenAiCompatible:ConnectTimeoutSeconds"] = "45",
            ["IncidentCompass:ModelGateway:OpenAiCompatible:FirstOutputTimeoutSeconds"] = "900",
            ["IncidentCompass:ModelGateway:OpenAiCompatible:StreamInactivityTimeoutSeconds"] = "120"
        });

        var options = provider.GetRequiredService<IOptions<OpenAiCompatibleModelClientOptions>>().Value;
        Assert.Equal(45, options.ConnectTimeoutSeconds);
        Assert.Equal(900, options.ResolveFirstOutputTimeoutSeconds());
        Assert.Equal(120, options.StreamInactivityTimeoutSeconds);

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(typeof(OpenAiCompatibleModelClient).Name);
        while (handler is DelegatingHandler delegatingHandler)
        {
            handler = delegatingHandler.InnerHandler!;
        }

        var socketHandler = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.Equal(TimeSpan.FromSeconds(45), socketHandler.ConnectTimeout);
        Assert.False(socketHandler.AllowAutoRedirect);
    }

    [Fact]
    public void AddInfrastructure_BothFirstOutputKeys_FailsValidationNamingBoth()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["IncidentCompass:ModelGateway:OpenAiCompatible:FirstOutputTimeoutSeconds"] = "600",
            ["IncidentCompass:ModelGateway:OpenAiCompatible:TimeoutSeconds"] = "300"
        });

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<OpenAiCompatibleModelClientOptions>>().Value);

        Assert.Contains(exception.Failures, failure =>
            failure.Contains("FirstOutputTimeoutSeconds", StringComparison.Ordinal) &&
            failure.Contains("TimeoutSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_DeprecatedKeyAloneIsAcceptedAndWarnsWithItsValueAndTheNewKey()
    {
        var logger = new RecordingLogger<OpenAiCompatibleModelClientOptionsValidator>();
        var validator = new OpenAiCompatibleModelClientOptionsValidator(
            Options.Create(new ModelGatewayOptions { Provider = "OpenAICompatible" }),
            logger);

        var result = validator.Validate(null, ValidOptions(timeoutSeconds: 300));

        Assert.True(result.Succeeded);
        var entry = logger.Single(DeprecatedTimeoutWarningEventId);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("300", entry.Message, StringComparison.Ordinal);
        Assert.Contains("FirstOutputTimeoutSeconds", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-api-key", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_NewKeyLogsNothing()
    {
        var logger = new RecordingLogger<OpenAiCompatibleModelClientOptionsValidator>();
        var validator = new OpenAiCompatibleModelClientOptionsValidator(
            Options.Create(new ModelGatewayOptions { Provider = "OpenAICompatible" }),
            logger);

        var result = validator.Validate(null, ValidOptions(firstOutputTimeoutSeconds: 600));

        Assert.True(result.Succeeded);
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// The Api appsettings set none of the call limits, so an operator who still overrides the
    /// deprecated key through the environment gets the old behaviour and a warning, not a start-up
    /// failure for having set both keys.
    /// </summary>
    [Fact]
    public void AddInfrastructure_LegacyOverrideOverShippedApiSettings_StartsWithTheLegacyValue()
    {
        var repositoryRoot = IncidentCompass.TestSupport.RepositoryRootLocator.Find();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(repositoryRoot, "src", "IncidentCompass.Api", "appsettings.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:OpenAiCompatible:TimeoutSeconds"] = "420"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<OpenAiCompatibleModelClientOptions>>().Value;

        Assert.Equal(420, options.ResolveFirstOutputTimeoutSeconds());
        Assert.True(options.UsesDeprecatedTimeoutSeconds);
    }

    private static OpenAiCompatibleModelClientOptions ValidOptions(
        int connectTimeoutSeconds = OpenAiCompatibleModelClientOptions.DefaultConnectTimeoutSeconds,
        int? firstOutputTimeoutSeconds = null,
        int streamInactivityTimeoutSeconds = OpenAiCompatibleModelClientOptions.DefaultStreamInactivityTimeoutSeconds,
        int? timeoutSeconds = null) =>
        new()
        {
            BaseUrl = "https://provider.example",
            ApiKey = "test-api-key",
            ConnectTimeoutSeconds = connectTimeoutSeconds,
            FirstOutputTimeoutSeconds = firstOutputTimeoutSeconds,
            StreamInactivityTimeoutSeconds = streamInactivityTimeoutSeconds,
            TimeoutSeconds = timeoutSeconds
        };

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        settings["IncidentCompass:ModelGateway:Provider"] = "OpenAiCompatible";
        settings["IncidentCompass:ModelGateway:OpenAiCompatible:ApiKey"] = "test-model-key";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
