using IncidentCompass.Application.Core.Text;
using IncidentCompass.Application.Investigation.Jobs;

namespace IncidentCompass.UnitTests;

public sealed class StrictJsonOutputNormalizerTests
{
    [Fact]
    public void TryNormalize_RawJsonReturnsTrimmedJson()
    {
        var accepted = StrictJsonOutputNormalizer.TryNormalize("  {\"ok\":true}\r\n", out var normalized);

        Assert.True(accepted);
        Assert.Equal("{\"ok\":true}", normalized);
    }

    [Theory]
    [InlineData("```json\n{\"ok\":true}\n```")]
    [InlineData("```JSON\r\n{\"ok\":true}\r\n```")]
    [InlineData("~~~\n{\"ok\":true}\n~~~")]
    [InlineData("~~~json\n{\"ok\":true}\n~~~")]
    public void TryNormalize_ExactlyOneOuterFenceReturnsInnerJson(string content)
    {
        var accepted = StrictJsonOutputNormalizer.TryNormalize(content, out var normalized);

        Assert.True(accepted);
        Assert.Equal("{\"ok\":true}", normalized);
    }

    [Theory]
    [InlineData("```json\n{\"ok\":true}\n~~~")]
    [InlineData("```json\n```json\n{\"ok\":true}\n```\n```")]
    [InlineData("```json\n{\"ok\":true}\n```\nextra prose")]
    [InlineData("````json\n{\"ok\":true}\n````")]
    public void TryNormalize_RejectsMismatchedNestedMultipleOrNonTripleFences(string content)
    {
        Assert.False(StrictJsonOutputNormalizer.TryNormalize(content, out _));
    }

    [Fact]
    public void SchemaValidator_RejectsExtraProseAroundOtherwiseValidJson()
    {
        const string schema = """{ "type": "object" }""";

        Assert.Throws<WorkerOutputValidationException>(() =>
            AnalysisWorkerOutputSchemaValidator.Validate(
                "Explanation follows.\n```json\n{}\n```",
                schema,
                "analysis"));
    }
}
