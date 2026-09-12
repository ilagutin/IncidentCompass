using System.Text.Json.Nodes;
using Google.Protobuf;
using IncidentCompass.Application.Intake.IngestSignal;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace IncidentCompass.Api;

internal static class OtlpTraceRequestMapper
{
    public static IReadOnlyCollection<IngestSignalCommand> Map(ExportTraceServiceRequest request)
    {
        var commands = new List<IngestSignalCommand>();
        foreach (var resourceSpans in request.ResourceSpans)
        {
            var resourceAttributes = OtlpAttributeMapper.ToJsonObject(resourceSpans.Resource?.Attributes ?? []);
            foreach (var scopeSpans in resourceSpans.ScopeSpans)
            {
                foreach (var span in scopeSpans.Spans)
                {
                    commands.Add(MapSpan(resourceAttributes, span));
                }
            }
        }

        return commands;
    }

    private static IngestSignalCommand MapSpan(JsonObject resourceAttributes, Span span)
    {
        ValidateIdentifier("trace_id", span.TraceId, expectedLength: 16, required: true);
        ValidateIdentifier("span_id", span.SpanId, expectedLength: 8, required: true);
        ValidateIdentifier("parent_span_id", span.ParentSpanId, expectedLength: 8, required: false);

        var attributes = OtlpAttributeMapper.ToJsonObject(span.Attributes);
        attributes["otel.resource"] = resourceAttributes.DeepClone();
        attributes["otel.span.kind"] = span.Kind.ToString();

        var errorType = OtlpAttributeMapper.GetString(attributes, "exception.type", "error.type");
        var errorMessage = OtlpAttributeMapper.GetString(attributes, "exception.message", "error.message");
        var statusIsError = span.Status?.Code == Status.Types.StatusCode.Error;
        attributes["errorType"] = errorType;
        attributes["errorMessage"] = errorMessage;
        attributes["operationName"] = span.Name;
        attributes["httpMethod"] = OtlpAttributeMapper.GetString(attributes, "http.request.method", "http.method");
        attributes["httpRoute"] = OtlpAttributeMapper.GetString(attributes, "http.route");
        attributes["httpStatusCode"] = OtlpAttributeMapper.GetInt(attributes, "http.response.status_code", "http.status_code");
        attributes["durationMs"] = DurationMilliseconds(span.StartTimeUnixNano, span.EndTimeUnixNano);

        var traceId = OtlpAttributeMapper.ToHex(span.TraceId);
        var spanId = OtlpAttributeMapper.ToHex(span.SpanId);
        return new IngestSignalCommand(
            SourceKind: "otel",
            ServiceName: OtlpAttributeMapper.GetString(resourceAttributes, "service.name"),
            Environment: OtlpAttributeMapper.GetString(resourceAttributes, "deployment.environment.name", "deployment.environment"),
            Severity: statusIsError || !string.IsNullOrWhiteSpace(errorType) ? "error" : "info",
            Summary: null,
            Description: errorMessage,
            ObservedAtUtc: OtlpAttributeMapper.ToDateTimeOffset(span.StartTimeUnixNano),
            TraceId: traceId,
            SpanId: spanId,
            ParentSpanId: OtlpAttributeMapper.ToHex(span.ParentSpanId),
            ExternalId: OtlpAttributeMapper.GetString(attributes, "incidentcompass.event.id") ?? $"{traceId}:{spanId}",
            Attributes: attributes,
            Payload: new JsonObject
            {
                ["spanName"] = span.Name,
                ["statusCode"] = span.Status?.Code.ToString(),
                ["statusMessage"] = span.Status?.Message
            });
    }

    private static void ValidateIdentifier(string name, ByteString value, int expectedLength, bool required)
    {
        if (!required && value.IsEmpty)
        {
            return;
        }

        if (value.Length != expectedLength || value.Span.IndexOfAnyExcept((byte)0) < 0)
        {
            throw new InvalidOtlpPayloadException($"OTLP {name} must contain exactly {expectedLength} bytes and must not be all zero.");
        }
    }

    private static int? DurationMilliseconds(ulong startTimeUnixNano, ulong endTimeUnixNano)
    {
        if (endTimeUnixNano < startTimeUnixNano)
        {
            return null;
        }

        var milliseconds = (endTimeUnixNano - startTimeUnixNano) / 1_000_000;
        return milliseconds <= int.MaxValue ? (int)milliseconds : int.MaxValue;
    }
}
