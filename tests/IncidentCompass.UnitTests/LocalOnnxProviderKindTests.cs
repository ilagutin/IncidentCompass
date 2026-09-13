using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

namespace IncidentCompass.UnitTests;

/// <summary>
/// <c>LocalOnnx</c> is the host provider kind of the in-process embedding model. Both gateways share
/// the parser, so the kind parses everywhere, but only the embedding gateway may select it; the
/// model gateway has no local chat adapter and refuses it while the host starts.
/// </summary>
public sealed class LocalOnnxProviderKindTests
{
    [Theory]
    [InlineData("LocalOnnx")]
    [InlineData("LOCAL_ONNX")]
    [InlineData("local-onnx")]
    [InlineData(" localonnx ")]
    public void ProviderKindParser_AcceptsLocalOnnxSpellings(string provider)
    {
        Assert.True(ProviderKindParser.TryParse(provider, out var kind));
        Assert.Equal(ProviderKind.LocalOnnx, kind);
        Assert.False(ProviderKindParser.IsOpenAiCompatible(provider));
    }

    [Fact]
    public void EmbeddingProviderOptionsValidator_AcceptsLocalOnnx()
    {
        var result = new EmbeddingProviderOptionsValidator().Validate(
            name: null,
            new EmbeddingOptions { Provider = "LocalOnnx" });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ModelGatewayProviderOptionsValidator_RefusesLocalOnnxAsAnEmbeddingOnlyProvider()
    {
        var result = new ModelGatewayProviderOptionsValidator().Validate(
            name: null,
            new ModelGatewayOptions { Provider = "LocalOnnx" });

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains("'LocalOnnx'", failure, StringComparison.Ordinal);
        Assert.Contains("unsupported", failure, StringComparison.Ordinal);
        Assert.Contains("embedding-only", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mock")]
    [InlineData("OpenAiCompatible")]
    public void ModelGatewayProviderOptionsValidator_StillAcceptsTheChatProviders(string provider)
    {
        var result = new ModelGatewayProviderOptionsValidator().Validate(
            name: null,
            new ModelGatewayOptions { Provider = provider });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task LocalOnnxEmbeddingClient_RefusesEveryCallAsNotInstalled()
    {
        var client = new LocalOnnxEmbeddingClient();

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            client.CreateEmbeddingAsync(
                new EmbeddingRequest(
                    "checkout timeout while calling the payment service",
                    "intfloat/multilingual-e5-small",
                    "local-onnx-test",
                    EmbeddingInputKind.Query,
                    "local-embed"),
                TestContext.Current.CancellationToken));

        Assert.Equal("embedding_model_not_installed", exception.ErrorCode);
        Assert.Equal("local-onnx", exception.Provider);
        Assert.Equal(ProviderFailureKind.Unavailable, exception.FailureKind);
        Assert.DoesNotContain("checkout timeout", exception.ToString(), StringComparison.Ordinal);
    }
}
