using System.Reflection;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The judge installs through the model store, and the store's error vocabulary is spelled for the
/// model it was written for: every constant in <c>LocalOnnxModelErrorCodes</c> begins
/// <c>embedding_model_</c>. None of those strings may reach a judge surface, where they would name
/// the wrong model, so they are translated at the judge's boundary rather than renamed at the
/// store's, which would change codes an embedding refusal has always carried.
/// </summary>
public sealed class LocalOnnxRelevanceJudgeErrorCodeTests
{
    private const string JudgePrefix = "relevance_judge_";

    /// <summary>
    /// The map's default arm exists so that a store code with no judge-side name cannot leak through
    /// untranslated. This is what keeps that arm unreachable: adding a code to the store fails here
    /// until somebody says what it means for a judge.
    /// </summary>
    [Fact]
    public void EveryStoreErrorCode_HasAJudgeCodeOfItsOwn()
    {
        var storeCodes = DeclaredStringConstants(typeof(LocalOnnxModelErrorCodes));
        Assert.NotEmpty(storeCodes);

        foreach (var storeCode in storeCodes)
        {
            var mapped = LocalOnnxRelevanceJudgeStoreErrorCodeMap.Map(storeCode);
            Assert.StartsWith(JudgePrefix, mapped, StringComparison.Ordinal);
            Assert.NotEqual(LocalOnnxRelevanceJudgeProvider.ModelUnusableErrorCode, mapped);
        }

        Assert.Equal(storeCodes.Length, storeCodes.Select(LocalOnnxRelevanceJudgeStoreErrorCodeMap.Map).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AnUnknownStoreCode_IsReportedAsUnusableRatherThanGuessedAt()
    {
        Assert.Equal(
            LocalOnnxRelevanceJudgeProvider.ModelUnusableErrorCode,
            LocalOnnxRelevanceJudgeStoreErrorCodeMap.Map("embedding_model_something_new"));
        Assert.Equal(
            LocalOnnxRelevanceJudgeProvider.ModelUnusableErrorCode,
            LocalOnnxRelevanceJudgeStoreErrorCodeMap.Map(null));
    }

    [Fact]
    public void NoJudgeErrorCode_IsSpelledForTheEmbeddingModel()
    {
        var judgeCodes = DeclaredStringConstants(typeof(LocalOnnxRelevanceJudgeProvider))
            .Where(static code => code != LocalOnnxRelevanceJudgeProvider.Name)
            .ToArray();
        Assert.NotEmpty(judgeCodes);

        Assert.All(judgeCodes, code => Assert.StartsWith(JudgePrefix, code, StringComparison.Ordinal));
    }

    /// <summary>
    /// The one refusal the verifier called actively misleading: a host that configures no judge has
    /// nothing wrong with its store, and reporting one sends an operator to look at a volume.
    /// </summary>
    [Fact]
    public async Task AHostWithNoJudgeDirectory_IsReportedAsNotConfiguredRatherThanAnUnavailableStore()
    {
        var reader = new LocalOnnxInstalledRelevanceJudgeReader(
            Options.Create(new LocalOnnxRelevanceJudgeOptions()),
            new LocalOnnxRelevanceJudgeInstallState(),
            LocalOnnxTestArtifacts.Store(ScriptedHttpMessageHandler.Refusing()));

        var lookup = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Null(lookup.Model);
        Assert.Equal(LocalOnnxRelevanceJudgeProvider.NotConfiguredErrorCode, lookup.ErrorCode);
        Assert.Contains("ModelDirectory is not set", lookup.Detail!, StringComparison.Ordinal);
    }

    private static string[] DeclaredStringConstants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .ToArray();
}
