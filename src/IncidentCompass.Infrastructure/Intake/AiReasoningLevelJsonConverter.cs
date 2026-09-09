using System.Text.Json;
using System.Text.Json.Serialization;
using IncidentCompass.Application.Core.ModelClients;

namespace IncidentCompass.Infrastructure.Intake;

internal sealed class AiReasoningLevelJsonConverter : JsonConverter<AiReasoningLevel>
{
    /// <summary>
    /// Key under which a rejected reasoning token is attached to <see cref="Exception.Data"/>.
    /// System.Text.Json rethrows the converter's own <see cref="JsonException"/> instance after
    /// stamping the JSON path on it, so the configuration loader can report both the setting path
    /// and the value the operator actually wrote. Configuration is operator-authored backend input,
    /// never model output, so echoing it back is safe.
    /// </summary>
    public const string ConfiguredValueKey = "IncidentCompass.ReasoningConfiguredValue";

    private const string ExpectedValues = "off, low, medium, high";

    public override AiReasoningLevel Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw UnsupportedValue(ReadRawJsonValue(ref reader));
        }

        var configuredValue = reader.GetString() ?? string.Empty;
        return configuredValue switch
        {
            "off" => AiReasoningLevel.Off,
            "low" => AiReasoningLevel.Low,
            "medium" => AiReasoningLevel.Medium,
            "high" => AiReasoningLevel.High,
            _ => throw UnsupportedValue(configuredValue)
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        AiReasoningLevel value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            AiReasoningLevel.Off => "off",
            AiReasoningLevel.Low => "low",
            AiReasoningLevel.Medium => "medium",
            AiReasoningLevel.High => "high",
            _ => throw new JsonException($"Reasoning must be one of: {ExpectedValues}.")
        });
    }

    private static JsonException UnsupportedValue(string configuredValue)
    {
        var exception = new JsonException(
            $"Reasoning value '{configuredValue}' must be one of: {ExpectedValues}.");
        exception.Data[ConfiguredValueKey] = configuredValue;
        return exception;
    }

    private static string ReadRawJsonValue(ref Utf8JsonReader reader)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        return value.RootElement.GetRawText();
    }
}
