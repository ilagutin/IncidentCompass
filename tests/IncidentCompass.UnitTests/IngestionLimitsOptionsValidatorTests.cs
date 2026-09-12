using IncidentCompass.Application.Intake.Configuration;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// <c>MaxSignalsPerExport</c> is the per-request record bound for OTLP ingestion, so a
/// misconfiguration that disables it (zero) or lifts it far past what one request should ever do
/// (above ten thousand) has to fail at startup rather than at ingestion time.
/// </summary>
public sealed class IngestionLimitsOptionsValidatorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(10_000)]
    public void Validate_AcceptsSignalsPerExportInsideTheAllowedRange(int maxSignalsPerExport)
    {
        var result = Validate(maxSignalsPerExport);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Validate_RejectsSignalsPerExportOutsideTheAllowedRange(int maxSignalsPerExport)
    {
        var result = Validate(maxSignalsPerExport);

        Assert.True(result.Failed);
        Assert.NotNull(result.FailureMessage);
        Assert.Contains("MaxSignalsPerExport", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DefaultOptionsAllowFiveHundredSignalsPerExport()
    {
        var options = new IngestionLimitsOptions();

        Assert.Equal(500, options.MaxSignalsPerExport);
        Assert.True(new IngestionLimitsOptionsValidator().Validate(null, options).Succeeded);
    }

    private static ValidateOptionsResult Validate(int maxSignalsPerExport) =>
        new IngestionLimitsOptionsValidator().Validate(
            null,
            new IngestionLimitsOptions { MaxSignalsPerExport = maxSignalsPerExport });
}
