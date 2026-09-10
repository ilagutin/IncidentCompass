namespace IncidentCompass.Application.Core.ModelGateway;

public sealed class ModelGatewayOptions
{
    public const string SectionName = "IncidentCompass:ModelGateway";

    public string Provider { get; init; } = "Mock";

    public double DefaultTemperature { get; init; } = 0.2;

    public int DefaultMaxOutputTokens { get; init; } = 512;

    public int MaxInputMessageCharacters { get; init; } = 8000;

    public double MinTemperature { get; init; }

    public double MaxTemperature { get; init; } = 1;

    public int MaxOutputTokensLimit { get; init; } = 2048;

    public int MaxCorrelationIdLength { get; init; } = 128;
}
