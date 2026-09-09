using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi;

namespace IncidentCompass.IntegrationTests;

public sealed class OpenAiCompatibleClientOptionsTests
{
    [Theory]
    [InlineData(OpenAiReasoningMode.Disabled)]
    [InlineData(OpenAiReasoningMode.ReasoningEffort)]
    [InlineData(OpenAiReasoningMode.ChatTemplateKwargs)]
    public void RequestFactory_PreservesPayloadWhenReasoningIsAbsent(OpenAiReasoningMode reasoningMode)
    {
        var request = CreateReasoningRequest(reasoning: null);

        var payload = OpenAiModelRequestFactory.CreatePayloadJson(request, CreateOptions(reasoningMode));

        Assert.Equal(
            "{\"model\":\"reasoning-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
            payload);
    }

    [Theory]
    [InlineData(AiReasoningLevel.Off, "none")]
    [InlineData(AiReasoningLevel.Low, "low")]
    [InlineData(AiReasoningLevel.Medium, "medium")]
    [InlineData(AiReasoningLevel.High, "high")]
    public void RequestFactory_MapsReasoningEffort(AiReasoningLevel reasoning, string expectedEffort)
    {
        var payload = OpenAiModelRequestFactory.CreatePayloadJson(
            CreateReasoningRequest(reasoning),
            CreateOptions(OpenAiReasoningMode.ReasoningEffort));

        Assert.Equal(
            $"{{\"model\":\"reasoning-model\",\"messages\":[{{\"role\":\"user\",\"content\":\"hello\"}}],\"reasoning_effort\":\"{expectedEffort}\"}}",
            payload);
    }

    [Theory]
    [InlineData(AiReasoningLevel.Off, false)]
    [InlineData(AiReasoningLevel.Low, true)]
    [InlineData(AiReasoningLevel.Medium, true)]
    [InlineData(AiReasoningLevel.High, true)]
    public void RequestFactory_MapsChatTemplateKwargs(AiReasoningLevel reasoning, bool expectedEnabled)
    {
        var payload = OpenAiModelRequestFactory.CreatePayloadJson(
            CreateReasoningRequest(reasoning),
            CreateOptions(OpenAiReasoningMode.ChatTemplateKwargs));

        Assert.Equal(
            $"{{\"model\":\"reasoning-model\",\"messages\":[{{\"role\":\"user\",\"content\":\"hello\"}}],\"chat_template_kwargs\":{{\"enable_thinking\":{expectedEnabled.ToString().ToLowerInvariant()}}}}}",
            payload);
    }

    [Theory]
    [InlineData(AiReasoningLevel.Off)]
    [InlineData(AiReasoningLevel.High)]
    public void RequestFactory_DisabledModeOmitsReasoning(AiReasoningLevel reasoning)
    {
        var payload = OpenAiModelRequestFactory.CreatePayloadJson(
            CreateReasoningRequest(reasoning),
            CreateOptions(OpenAiReasoningMode.Disabled));

        Assert.Equal(
            "{\"model\":\"reasoning-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
            payload);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unmapped-provider")]
    public void RequestFactory_MissingProviderIdOrMappingOmitsReasoningWithoutModelInference(
        string? providerId)
    {
        var request = CreateReasoningRequest(AiReasoningLevel.High) with
        {
            Model = "o3-reasoning-model",
            ProviderId = providerId
        };

        var payload = OpenAiModelRequestFactory.CreatePayloadJson(
            request,
            CreateOptions(OpenAiReasoningMode.ReasoningEffort));

        Assert.Equal(
            "{\"model\":\"o3-reasoning-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
            payload);
    }

    [Fact]
    public void ModelClientOptions_DefaultsRetryDelayCeilingToFiveSeconds()
    {
        var options = new OpenAiCompatibleModelClientOptions();

        Assert.Equal(5, options.MaxRetryDelaySeconds);
        Assert.Empty(options.ReasoningModes);
    }

    [Fact]
    public void ModelClientOptions_RejectsUnknownReasoningMode()
    {
        var options = new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            ReasoningModes = new Dictionary<string, OpenAiReasoningMode>
            {
                ["local-oai"] = (OpenAiReasoningMode)99
            }
        };

        Assert.False(options.IsValid());
    }

    [Fact]
    public void ModelClientOptions_RejectsBlankReasoningModeProviderId()
    {
        var options = new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            ReasoningModes = new Dictionary<string, OpenAiReasoningMode>
            {
                [" "] = OpenAiReasoningMode.ReasoningEffort
            }
        };

        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData(OpenAiReasoningMode.Disabled)]
    [InlineData(OpenAiReasoningMode.ReasoningEffort)]
    [InlineData(OpenAiReasoningMode.ChatTemplateKwargs)]
    public void ModelClientOptions_AcceptsKnownReasoningModes(OpenAiReasoningMode reasoningMode)
    {
        var options = new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            ReasoningModes = new Dictionary<string, OpenAiReasoningMode>
            {
                ["local-oai"] = reasoningMode
            }
        };

        Assert.True(options.IsValid());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void ModelClientOptions_RejectsInvalidRetryDelayCeiling(int maxRetryDelaySeconds)
    {
        var options = new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            MaxRetryDelaySeconds = maxRetryDelaySeconds
        };

        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    public void ModelClientOptions_AcceptsValidRetryDelayCeiling(int maxRetryDelaySeconds)
    {
        var options = new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            MaxRetryDelaySeconds = maxRetryDelaySeconds
        };

        Assert.True(options.IsValid());
    }

    [Theory]
    [InlineData("/v1/chat/completions", "https://api.openai.com/v1/chat/completions")]
    [InlineData("v1/chat/completions", "https://api.openai.com/v1/chat/completions")]
    public void ModelClientOptions_AcceptsRelativeEndpointPath(
        string endpointPath,
        string expectedEndpoint)
    {
        var options = new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            ChatCompletionsPath = endpointPath
        };

        var created = options.TryCreateEndpointUri(out var endpointUri);

        Assert.True(created);
        Assert.True(options.IsValid());
        Assert.Equal(expectedEndpoint, endpointUri!.ToString().TrimEnd('/'));
    }

    [Theory]
    [InlineData("/v1/embeddings", "https://api.openai.com/v1/embeddings")]
    [InlineData("v1/embeddings", "https://api.openai.com/v1/embeddings")]
    public void EmbeddingClientOptions_AcceptsRelativeEndpointPath(
        string endpointPath,
        string expectedEndpoint)
    {
        var options = new OpenAiCompatibleEmbeddingClientOptions
        {
            ApiKey = "test-api-key",
            EmbeddingsPath = endpointPath
        };

        var created = options.TryCreateEndpointUri(out var endpointUri);

        Assert.True(created);
        Assert.True(options.IsValid());
        Assert.Equal(expectedEndpoint, endpointUri!.ToString().TrimEnd('/'));
    }

    [Fact]
    public void ModelClientOptions_AllowsHostDockerInternalWhenLoopbackOverrideIsEnabled()
    {
        var allowed = new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            BaseUrl = "http://host.docker.internal:1234",
            AllowInsecureHttpForLoopback = true
        };
        var denied = new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            BaseUrl = "http://host.docker.internal:1234"
        };

        Assert.True(allowed.TryCreateEndpointUri(out var endpointUri));
        Assert.True(allowed.IsValid());
        Assert.Equal("http://host.docker.internal:1234/v1/chat/completions", endpointUri!.ToString().TrimEnd('/'));
        Assert.False(denied.TryCreateEndpointUri(out _));
        Assert.False(denied.IsValid());
    }

    [Theory]
    [InlineData("https://provider.example/v1/chat/completions")]
    [InlineData("//provider.example/v1/chat/completions")]
    public void ModelClientOptions_RejectsAbsoluteEndpointPath(string endpointPath)
    {
        var options = new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            ChatCompletionsPath = endpointPath
        };

        Assert.False(options.TryCreateEndpointUri(out _));
        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData("https://provider.example/v1/embeddings")]
    [InlineData("//provider.example/v1/embeddings")]
    public void EmbeddingClientOptions_RejectsAbsoluteEndpointPath(string endpointPath)
    {
        var options = new OpenAiCompatibleEmbeddingClientOptions
        {
            ApiKey = "test-api-key",
            EmbeddingsPath = endpointPath
        };

        Assert.False(options.TryCreateEndpointUri(out _));
        Assert.False(options.IsValid());
    }

    private static AiModelRequest CreateReasoningRequest(AiReasoningLevel? reasoning)
    {
        return new AiModelRequest(
            CorrelationId: "payload-test",
            Model: "reasoning-model",
            Messages: [new AiChatMessage(AiMessageRole.User, "hello")],
            ProviderId: "local-oai",
            Reasoning: reasoning);
    }

    private static OpenAiCompatibleModelClientOptions CreateOptions(OpenAiReasoningMode reasoningMode)
    {
        return new OpenAiCompatibleModelClientOptions
        {
            ApiKey = "test-api-key",
            ReasoningModes = new Dictionary<string, OpenAiReasoningMode>
            {
                ["local-oai"] = reasoningMode
            }
        };
    }
}
