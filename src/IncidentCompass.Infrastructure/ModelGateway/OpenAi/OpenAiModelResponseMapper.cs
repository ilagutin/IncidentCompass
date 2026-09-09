using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi.Dtos;
using IncidentCompass.Infrastructure.OpenAiCompatible;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

internal static class OpenAiModelResponseMapper
{
    public static AiModelResponse Map(
        string responseContent,
        AiModelRequest request)
    {
        var completion = JsonSerializer.Deserialize<OpenAiChatCompletionResponse>(
            responseContent,
            OpenAiCompatibleJson.Options);
        var returnedModel = string.IsNullOrWhiteSpace(completion?.Model) ? null : completion.Model;
        var usage = completion?.Usage is null
            ? null
            : new AiModelUsage(
                completion.Usage.PromptTokens,
                completion.Usage.CompletionTokens,
                completion.Usage.TotalTokens,
                completion.Usage.CompletionTokensDetails?.ReasoningTokens);
        var choices = completion?.Choices;
        var choice = choices is { Count: > 0 } ? choices[0] : null;
        var message = choice?.Message;
        var content = message?.Content;
        var proposedToolCalls = MapToolCalls(message?.ToolCalls, usage, returnedModel);

        if (string.IsNullOrWhiteSpace(content) && proposedToolCalls.Length == 0)
        {
            if (string.Equals(choice?.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                throw OpenAiModelErrorMapper.OutputLimitReached(usage, returnedModel);
            }

            throw OpenAiModelErrorMapper.EmptyResponse(usage, returnedModel);
        }

        return new AiModelResponse(
            Content: content ?? string.Empty,
            Model: returnedModel ?? request.Model,
            Provider: OpenAiModelProvider.Name,
            Usage: usage,
            CorrelationId: request.CorrelationId,
            ProposedToolCalls: proposedToolCalls);
    }

    private static AiToolCall[] MapToolCalls(
        IReadOnlyList<OpenAiToolCall>? toolCalls,
        AiModelUsage? usage,
        string? returnedModel)
    {
        if (toolCalls is null || toolCalls.Count == 0)
        {
            return [];
        }

        var mappedToolCalls = new AiToolCall[toolCalls.Count];
        for (var index = 0; index < toolCalls.Count; index++)
        {
            mappedToolCalls[index] = ToAiToolCall(toolCalls[index], usage, returnedModel);
        }

        return mappedToolCalls;
    }

    private static AiToolCall ToAiToolCall(
        OpenAiToolCall? toolCall,
        AiModelUsage? usage,
        string? returnedModel)
    {
        if (toolCall is null ||
            string.IsNullOrWhiteSpace(toolCall.Id) ||
            !string.Equals(toolCall.Type, "function", StringComparison.Ordinal) ||
            toolCall.Function is null ||
            string.IsNullOrWhiteSpace(toolCall.Function.Name))
        {
            throw OpenAiModelErrorMapper.InvalidToolCall(usage, returnedModel);
        }

        return new AiToolCall(
            toolCall.Id,
            toolCall.Function.Name,
            "v1",
            ReadArguments(toolCall.Function.Arguments, usage, returnedModel));
    }

    private static JsonElement ReadArguments(
        string? argumentsJson,
        AiModelUsage? usage,
        string? returnedModel)
    {
        try
        {
            if (!StrictJsonOutputNormalizer.TryNormalize(argumentsJson ?? string.Empty, out var normalizedJson))
            {
                throw OpenAiModelErrorMapper.InvalidToolCall(usage, returnedModel);
            }

            using var argumentsDocument = JsonDocument.Parse(normalizedJson);
            if (argumentsDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw OpenAiModelErrorMapper.InvalidToolCall(usage, returnedModel);
            }

            return argumentsDocument.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw OpenAiModelErrorMapper.InvalidToolCall(usage, returnedModel, exception);
        }
    }
}
