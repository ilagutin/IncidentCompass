using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The local adapter end to end on the committed fixture model: tokenization, the ONNX run, mean
/// pooling, normalization and the window cap, the encoded model identity it reports, plus every
/// refusal with its code. Assertions are about dimensions, norms, token counts, names and codes; no
/// test compares vector values.
/// </summary>
public sealed class LocalOnnxEmbeddingAdapterTests : IAsyncLifetime
{
    private const string LocalProviderId = "local-embed";

    private LocalOnnxFixtureModel? fixture;
    private LocalOnnxModelRuntime? runtime;

    private LocalOnnxFixtureModel Fixture => fixture!;

    private LocalOnnxModelRuntime Runtime => runtime!;

    public async ValueTask InitializeAsync()
    {
        fixture = await LocalOnnxFixtureModel.InstallAsync(TestContext.Current.CancellationToken);
        runtime = new LocalOnnxModelRuntime(Options.Create(fixture.Options));
    }

    public ValueTask DisposeAsync()
    {
        runtime?.Dispose();
        fixture?.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CreateEmbedding_ReturnsAUnitVectorOfTheManifestDimensionsUnderTheEncodedIdentity()
    {
        var response = await CreateClient().CreateEmbeddingAsync(
            Request("checkout timeout while calling the payment service"),
            TestContext.Current.CancellationToken);

        Assert.Equal(Fixture.FixtureManifest.Dimensions, response.Vector.Count);
        Assert.InRange(Norm(response.Vector), 0.999, 1.001);
        Assert.Equal(LocalOnnxModelIdentity.Describe(Fixture.FixtureManifest), response.Model);
        Assert.True(EncodedEmbeddingModelIdentity.TryParse(response.Model, out var modelId, out var digestPrefix));
        Assert.Equal(Fixture.FixtureManifest.Id, modelId);
        Assert.Equal(Fixture.FixtureManifest.ModelFile.Sha256[..16], digestPrefix);
        Assert.Equal(LocalOnnxEmbeddingProvider.Name, response.Provider);
        Assert.Equal("local-onnx-adapter-test", response.CorrelationId);
        Assert.InRange(response.InputTokens!.Value, 3, Fixture.FixtureManifest.MaxTokens);
    }

    [Fact]
    public async Task CreateEmbedding_AcceptsTheInstalledModelByItsEncodedIdentity()
    {
        var encoded = LocalOnnxModelIdentity.Describe(Fixture.FixtureManifest);

        var response = await CreateClient().CreateEmbeddingAsync(
            Request("checkout timeout", model: encoded),
            TestContext.Current.CancellationToken);

        Assert.Equal(encoded, response.Model);
    }

    [Fact]
    public async Task CreateEmbedding_WithNoInstallPass_ReadsTheVerifiedModelFromTheStore()
    {
        var state = new LocalOnnxModelInstallState();

        var response = await CreateClient(state).CreateEmbeddingAsync(
            Request("checkout timeout"),
            TestContext.Current.CancellationToken);

        Assert.Equal(Fixture.FixtureManifest.Dimensions, response.Vector.Count);
        Assert.Equal(LocalOnnxModelInstallStatus.Installed, state.Snapshot.Status);
    }

    [Fact]
    public async Task CreateEmbedding_TruncatesAnInputLongerThanTheModelWindow()
    {
        var response = await CreateClient().CreateEmbeddingAsync(
            Request(string.Concat(Enumerable.Repeat("checkout timeout ", 200)), EmbeddingInputKind.Passage),
            TestContext.Current.CancellationToken);

        Assert.Equal(Fixture.FixtureManifest.MaxTokens, response.InputTokens);
        Assert.Equal(Fixture.FixtureManifest.Dimensions, response.Vector.Count);
        Assert.InRange(Norm(response.Vector), 0.999, 1.001);
    }

    /// <summary>
    /// The reason the cap exists: the fixture graph, like the real model, fails on a sequence longer
    /// than its position table. A manifest that claims a larger window lets the long input through,
    /// and the run fails with the named inference code instead of producing a vector.
    /// </summary>
    [Fact]
    public async Task CreateEmbedding_WithAWindowLargerThanTheGraph_FailsWithTheInferenceCode()
    {
        var overstated = Fixture.Installed with
        {
            Manifest = Fixture.Installed.Manifest with { MaxTokens = 512 }
        };

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            CreateClient(InstalledState(overstated)).CreateEmbeddingAsync(
                Request(string.Concat(Enumerable.Repeat("checkout timeout ", 200)), EmbeddingInputKind.Passage),
                TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxEmbeddingProvider.InferenceFailedErrorCode, exception.ErrorCode);
    }

    /// <summary>
    /// An output the adapter cannot use, here a vector width other than the manifest declares, leaves
    /// as the port's exception with its own code rather than as a vector of the wrong shape.
    /// </summary>
    [Fact]
    public async Task CreateEmbedding_WhenTheOutputWidthIsNotTheManifestWidth_IsRefusedWithTheDimensionsCode()
    {
        var misdeclared = Fixture.Installed with
        {
            Manifest = Fixture.Installed.Manifest with { Dimensions = Fixture.FixtureManifest.Dimensions * 2 }
        };

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            CreateClient(InstalledState(misdeclared)).CreateEmbeddingAsync(
                Request("checkout timeout"),
                TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxEmbeddingProvider.DimensionsMismatchErrorCode, exception.ErrorCode);
        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
    }

    [Fact]
    public async Task CreateEmbedding_WhenTheTriageConfigurationCannotBeRead_IsRefusedWithACode()
    {
        var client = new LocalOnnxEmbeddingClient(Reader(InstalledState()), Runtime, new ConfigurationRepository(null));

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            client.CreateEmbeddingAsync(Request("checkout timeout"), TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxEmbeddingProvider.ConfigurationReadFailedErrorCode, exception.ErrorCode);
        Assert.Equal(ProviderFailureKind.Unavailable, exception.FailureKind);
        Assert.True(ProviderOutageExceptionClassifier.IsProviderOutage(exception));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task CreateEmbedding_RefusesAnUndefinedInputKind(int kind)
    {
        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            CreateClient().CreateEmbeddingAsync(
                Request("checkout timeout", (EmbeddingInputKind)kind),
                TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxEmbeddingProvider.InputKindInvalidErrorCode, exception.ErrorCode);
        Assert.Equal(ProviderFailureKind.RejectedRequest, exception.FailureKind);
    }

    [Fact]
    public async Task CreateEmbedding_RefusesARouteProviderOfAnotherKindNamingBoth()
    {
        var client = CreateClient(providers: [("local-oai", "OpenAICompatible")]);

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            client.CreateEmbeddingAsync(
                Request("checkout timeout", providerId: "local-oai"),
                TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxEmbeddingProvider.RouteProviderMismatchErrorCode, exception.ErrorCode);
        Assert.Contains("local-oai", exception.Message, StringComparison.Ordinal);
        Assert.Contains("OpenAICompatible", exception.Message, StringComparison.Ordinal);
        Assert.Contains("LocalOnnx", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("checkout timeout", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateEmbedding_RefusesARouteProviderWithNoEntry()
    {
        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            CreateClient(providers: []).CreateEmbeddingAsync(
                Request("checkout timeout"),
                TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxEmbeddingProvider.RouteProviderMismatchErrorCode, exception.ErrorCode);
    }

    [Fact]
    public async Task CreateEmbedding_WithNoRouteProvider_ReadsNoConfiguration()
    {
        var client = new LocalOnnxEmbeddingClient(Reader(InstalledState()), Runtime, new ConfigurationRepository(null));

        var response = await client.CreateEmbeddingAsync(
            Request("checkout timeout", providerId: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(Fixture.FixtureManifest.Dimensions, response.Vector.Count);
    }

    /// <summary>
    /// A request for another model, which is what <c>memory_search</c> sends when the installed model
    /// is not the one its route names, is a configuration state an operator fixes, not a provider
    /// outage: it is normalized to the corpus state's code, keeps the adapter's own code as the
    /// provider error code, and does not feed the outage pause.
    /// </summary>
    [Fact]
    public async Task CreateEmbedding_RefusesAModelOtherThanTheInstalledOneAsAConfigurationState()
    {
        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            CreateClient().CreateEmbeddingAsync(
                Request("checkout timeout", model: "intfloat/multilingual-e5-base"),
                TestContext.Current.CancellationToken));

        Assert.Equal(MemoryEmbeddingModelErrorCodes.Mismatch, exception.ErrorCode);
        Assert.Equal(LocalOnnxEmbeddingProvider.ModelMismatchErrorCode, exception.ProviderErrorCode);
        Assert.Equal(ProviderFailureKind.ConfigurationRequired, exception.FailureKind);
        Assert.False(ProviderOutageExceptionClassifier.IsProviderOutage(exception));
        Assert.Contains("intfloat/multilingual-e5-base", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Fixture.FixtureManifest.Id, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateEmbedding_BeforeAnyInstall_IsRefusedAsNotInstalled()
    {
        using var emptyDirectory = new LocalOnnxTestDirectory();
        var client = new LocalOnnxEmbeddingClient(
            LocalModelTestSupport.Reader(new LocalOnnxModelInstallState(), emptyDirectory.FullPath),
            Runtime,
            new ConfigurationRepository([(LocalProviderId, "LocalOnnx")]));

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            client.CreateEmbeddingAsync(Request("checkout timeout"), TestContext.Current.CancellationToken));

        Assert.Equal(MemoryEmbeddingModelErrorCodes.Unavailable, exception.ErrorCode);
        Assert.Equal(LocalOnnxEmbeddingProvider.ModelNotInstalledErrorCode, exception.ProviderErrorCode);
        Assert.Equal(ProviderFailureKind.ConfigurationRequired, exception.FailureKind);
        Assert.False(ProviderOutageExceptionClassifier.IsProviderOutage(exception));
    }

    [Fact]
    public async Task CreateEmbedding_AfterAFailedInstall_IsRefusedAsUnavailableWithTheInstallCode()
    {
        var state = new LocalOnnxModelInstallState();
        state.RecordFailed(LocalOnnxModelErrorCodes.DigestMismatch, "The onnx file has the wrong digest.");

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            CreateClient(state).CreateEmbeddingAsync(Request("checkout timeout"), TestContext.Current.CancellationToken));

        Assert.Equal(MemoryEmbeddingModelErrorCodes.Unavailable, exception.ErrorCode);
        Assert.Equal(LocalOnnxModelErrorCodes.DigestMismatch, exception.ProviderErrorCode);
        Assert.Equal(ProviderFailureKind.ConfigurationRequired, exception.FailureKind);
        Assert.False(ProviderOutageExceptionClassifier.IsProviderOutage(exception));
    }

    [Fact]
    public async Task CreateEmbedding_WhenTheModelFileCannotBeLoaded_IsRefusedWithTheLoadCode()
    {
        var unloadable = Fixture.Installed with { ModelFilePath = Fixture.Installed.TokenizerFilePath };

        var exception = await Assert.ThrowsAsync<EmbeddingClientException>(() =>
            CreateClient(InstalledState(unloadable)).CreateEmbeddingAsync(
                Request("checkout timeout"),
                TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxEmbeddingProvider.ModelLoadFailedErrorCode, exception.ErrorCode);
    }

    [Fact]
    public async Task CreateEmbedding_WithACancelledToken_IsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient().CreateEmbeddingAsync(Request("checkout timeout"), cancellation.Token));
    }

    [Fact]
    public async Task CreateEmbedding_ConcurrentCallsAllComplete()
    {
        var client = CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            client.CreateEmbeddingAsync(
                Request("checkout timeout number " + index, index % 2 == 0 ? EmbeddingInputKind.Query : EmbeddingInputKind.Passage),
                TestContext.Current.CancellationToken)));

        Assert.All(responses, response =>
        {
            Assert.Equal(Fixture.FixtureManifest.Dimensions, response.Vector.Count);
            Assert.InRange(Norm(response.Vector), 0.999, 1.001);
        });
    }

    private LocalOnnxModelInstallState InstalledState(LocalOnnxInstalledModel? installed = null)
    {
        var state = new LocalOnnxModelInstallState();
        state.RecordInstalled(installed ?? Fixture.Installed);
        return state;
    }

    private LocalOnnxInstalledModelReader Reader(LocalOnnxModelInstallState state) =>
        LocalModelTestSupport.Reader(state, Fixture.Options.ModelDirectory);

    private LocalOnnxEmbeddingClient CreateClient(
        LocalOnnxModelInstallState? state = null,
        (string Id, string Kind)[]? providers = null) =>
        new(
            Reader(state ?? InstalledState()),
            Runtime,
            new ConfigurationRepository(providers ?? [(LocalProviderId, "LocalOnnx")]));

    private EmbeddingRequest Request(
        string input,
        EmbeddingInputKind kind = EmbeddingInputKind.Query,
        string? providerId = LocalProviderId,
        string? model = null) =>
        new(input, model ?? Fixture.FixtureManifest.Id, "local-onnx-adapter-test", kind, providerId);

    private static double Norm(IReadOnlyList<float> vector) =>
        Math.Sqrt(vector.Sum(static value => (double)value * value));

    private sealed class ConfigurationRepository((string Id, string Kind)[]? providers) : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken)
        {
            if (providers is null)
            {
                throw new InvalidOperationException("The triage configuration was read, but this test expects no read.");
            }

            return Task.FromResult(TestModelProviderProfiles.CreateConfiguration(
                providers.ToDictionary(
                    static provider => provider.Id,
                    static provider => new TriageProviderSettings(provider.Kind, Endpoint: null, ApiKeySecretRef: null),
                    StringComparer.Ordinal)));
        }

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
            GetCurrentAsync(cancellationToken);
    }
}
