using System.Reflection;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Memory;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IncidentCompass.UnitTests;

/// <summary>
/// How the relevance judge is composed on the Worker-only model host: the port is bound, the judge's
/// install pass starts behind the embedding model's install and the memory seed pass, and the two
/// models keep separate install states.
/// </summary>
/// <remarks>
/// The separate state is the part worth a test of its own. A
/// <see cref="LocalOnnxModelInstallState" /> records one install and names no model, so one shared
/// instance would make the judge report the embedding model as installed and, worse, make the judge's
/// own install overwrite the snapshot the embedding adapter reads. The Api's side of the same rule is
/// asserted by <see cref="ModelHostArchitectureTests" />, which refuses the judge's namespace, port
/// and options type anywhere under <c>src/IncidentCompass.Api</c>; the Api host is composed by
/// <c>AddApi</c>, which never calls the seam these tests exercise.
/// </remarks>
public sealed class LocalOnnxRelevanceJudgeCompositionTests
{
    [Fact]
    public void AddEmbeddingHost_BindsTheRelevanceJudgePort()
    {
        using var provider = BuildModelHost();

        Assert.IsType<LocalOnnxRelevanceJudgeClient>(provider.GetRequiredService<IMemoryRelevanceJudge>());
    }

    [Fact]
    public void AddEmbeddingHost_StartsTheJudgeInstallAfterTheModelInstallAndTheSeedPass()
    {
        var services = new ServiceCollection();
        services.AddEmbeddingHost(new ConfigurationBuilder().Build());

        var hostedServiceTypes = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(static descriptor => descriptor.ImplementationType)
            .ToList();
        var modelInstallIndex = hostedServiceTypes.IndexOf(typeof(LocalOnnxModelInstallHostedService));
        var seedIndex = hostedServiceTypes.IndexOf(typeof(MemorySeedHostedService));
        var judgeInstallIndex = hostedServiceTypes.IndexOf(typeof(LocalOnnxRelevanceJudgeInstallHostedService));

        Assert.True(judgeInstallIndex >= 0, "The judge install hosted service is not registered.");
        Assert.True(judgeInstallIndex > modelInstallIndex, "The judge install would start before the model install.");
        Assert.True(judgeInstallIndex > seedIndex, "The judge install would start before the first memory seed pass.");
    }

    [Fact]
    public void TheEmbeddingModelAndTheJudgeDoNotShareOneInstallState()
    {
        using var provider = BuildModelHost();

        Assert.NotSame(
            provider.GetRequiredService<LocalOnnxModelInstallState>(),
            provider.GetRequiredService<LocalOnnxRelevanceJudgeInstallState>());
    }

    [Fact]
    public void InstallingTheJudgeLeavesTheEmbeddingModelReportingNothingInstalled()
    {
        using var provider = BuildModelHost();
        var embeddingState = provider.GetRequiredService<LocalOnnxModelInstallState>();

        provider.GetRequiredService<LocalOnnxRelevanceJudgeInstallState>().RecordInstalled(JudgeModel());

        Assert.Equal(LocalOnnxModelInstallStatus.NotStarted, embeddingState.Snapshot.Status);
        Assert.Null(embeddingState.Snapshot.Model);
    }

    [Fact]
    public void InstallingTheEmbeddingModelLeavesTheJudgeReportingNothingInstalled()
    {
        using var provider = BuildModelHost();
        var judgeState = provider.GetRequiredService<LocalOnnxRelevanceJudgeInstallState>();

        provider.GetRequiredService<LocalOnnxModelInstallState>().RecordInstalled(EmbeddingModel());

        Assert.Equal(LocalOnnxModelInstallStatus.NotStarted, judgeState.Snapshot.Status);
        Assert.Null(judgeState.Snapshot.Model);
    }

    /// <summary>
    /// The same rule through the reader the adapter actually calls: an installed embedding model must
    /// not come back as the installed judge. With no judge directory configured the honest answer is
    /// that this host runs no judge, and that is what a correctly wired reader gives.
    /// </summary>
    [Fact]
    public async Task TheJudgeReader_DoesNotReportTheInstalledEmbeddingModel()
    {
        using var provider = BuildModelHost();
        provider.GetRequiredService<LocalOnnxModelInstallState>().RecordInstalled(EmbeddingModel());

        var lookup = await provider.GetRequiredService<LocalOnnxInstalledRelevanceJudgeReader>()
            .ReadAsync(TestContext.Current.CancellationToken);

        Assert.Null(lookup.Model);
        Assert.Equal(LocalOnnxRelevanceJudgeProvider.NotConfiguredErrorCode, lookup.ErrorCode);
    }

    /// <summary>
    /// <see cref="LocalOnnxModelInstallState" /> is not sealed for one reason only: a second model
    /// needs a distinct type to be resolved by, and the judge is that second model. The reason lives
    /// in a comment, which is not something a third model's author has to read. This asserts the shape
    /// the comment describes, so opening the base type up further, by making a member virtual or
    /// protected, or by letting the derived type be inherited in turn, fails here instead of quietly
    /// turning an identity marker into an extension point.
    /// </summary>
    [Fact]
    public void TheJudgeStateIsAnIdentityMarkerAndNotAnExtensionPoint()
    {
        Assert.True(
            typeof(LocalOnnxRelevanceJudgeInstallState).IsSealed,
            "The judge's install state must stay sealed; it exists to be resolved, not derived from.");
        Assert.Equal(typeof(LocalOnnxModelInstallState), typeof(LocalOnnxRelevanceJudgeInstallState).BaseType);

        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var openings = typeof(LocalOnnxModelInstallState)
            .GetMembers(declared)
            .Where(static member => member is MethodBase { IsVirtual: true } or MethodBase { IsFamily: true } or
                MethodBase { IsFamilyOrAssembly: true } or FieldInfo { IsFamily: true } or
                FieldInfo { IsFamilyOrAssembly: true })
            .Select(static member => member.Name)
            .ToArray();

        Assert.Empty(openings);
    }

    private static ServiceProvider BuildModelHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEmbeddingHost(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static LocalOnnxInstalledModel EmbeddingModel() =>
        Installed(new LocalOnnxEmbeddingOptions().CreatePin());

    private static LocalOnnxInstalledModel JudgeModel() =>
        Installed(new LocalOnnxRelevanceJudgeOptions().CreatePin());

    private static LocalOnnxInstalledModel Installed(LocalOnnxModelPin pin) =>
        new(LocalOnnxModelStore.CreateManifest(pin), "unused-model.onnx", "unused-tokenizer.model");
}
