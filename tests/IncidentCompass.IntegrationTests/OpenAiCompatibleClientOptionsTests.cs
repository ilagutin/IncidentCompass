using IncidentCompass.Infrastructure.Configuration;

namespace IncidentCompass.IntegrationTests;

public sealed class OpenAiCompatibleClientOptionsTests
{
    [Fact]
    public void ModelClientOptions_DefaultsRetryDelayCeilingToFiveSeconds()
    {
        var options = new OpenAiCompatibleModelClientOptions();

        Assert.Equal(5, options.MaxRetryDelaySeconds);
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
}
