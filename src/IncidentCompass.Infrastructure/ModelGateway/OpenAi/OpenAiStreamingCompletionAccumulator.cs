using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi.Dtos;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

/// <summary>
/// Assembles the chunks of a streamed chat completion into the completion a non-streaming provider
/// would have answered with, so both shapes are validated by one response mapping.
/// </summary>
/// <remarks>
/// <para>
/// Only choice <c>0</c> is read, as the non-streamed path reads only the first choice; a chunk choice
/// without an index is choice <c>0</c>. Content deltas are appended. The first non-blank model is
/// kept, the last usage chunk and the last finish reason are kept, and reasoning deltas are not kept.
/// </para>
/// <para>
/// Tool-call fragments are merged by index, and a fragment that cannot belong to a well-formed call
/// ends the stream as a malformed tool call at once: a fragment without an index, an index that skips
/// past the next unstarted one, a new index whose first fragment carries no id, or a fragment that
/// names a different id, type or function name than its call already holds.
/// </para>
/// </remarks>
internal sealed class OpenAiStreamingCompletionAccumulator
{
    private readonly List<OpenAiStreamingToolCallBuilder> toolCalls = [];
    private StringBuilder? content;
    private string? model;
    private OpenAiUsage? usage;
    private string? finishReason;
    private bool sawChoice;

    /// <summary>Whether any chunk carried choice <c>0</c>.</summary>
    public bool HasChoice => sawChoice;

    /// <summary>Whether choice <c>0</c> has reported why it finished.</summary>
    public bool HasFinishReason => finishReason is not null;

    /// <summary>Takes in one parsed <c>data</c> event.</summary>
    /// <exception cref="AiModelException">
    /// The chunk carries an error, or a tool-call fragment that cannot be merged.
    /// </exception>
    public void Append(OpenAiChatCompletionChunk chunk)
    {
        if (model is null && !string.IsNullOrWhiteSpace(chunk.Model))
        {
            model = chunk.Model;
        }

        if (chunk.Usage is not null)
        {
            usage = chunk.Usage;
        }

        if (chunk.Error is { ValueKind: not JsonValueKind.Null } error)
        {
            throw OpenAiModelErrorMapper.StreamError(error, OpenAiModelResponseMapper.MapUsage(usage), model);
        }

        if (chunk.Choices is null)
        {
            return;
        }

        foreach (var choice in chunk.Choices)
        {
            if (choice is null || (choice.Index ?? 0) != 0)
            {
                continue;
            }

            AppendChoice(choice);
        }
    }

    /// <summary>The assembled completion, with no choice at all when choice <c>0</c> never arrived.</summary>
    public OpenAiChatCompletionResponse ToResponse()
    {
        if (!sawChoice)
        {
            return new OpenAiChatCompletionResponse(model, Choices: null, usage);
        }

        var message = new OpenAiResponseMessage(
            content?.ToString(),
            toolCalls.Count == 0 ? null : toolCalls.Select(static builder => builder.Build()).ToArray());
        return new OpenAiChatCompletionResponse(model, [new OpenAiChoice(message, finishReason)], usage);
    }

    private void AppendChoice(OpenAiChunkChoice choice)
    {
        sawChoice = true;
        if (choice.FinishReason is not null)
        {
            finishReason = choice.FinishReason;
        }

        if (choice.Delta is null)
        {
            return;
        }

        if (choice.Delta.Content is not null)
        {
            (content ??= new StringBuilder()).Append(choice.Delta.Content);
        }

        if (choice.Delta.ToolCalls is null)
        {
            return;
        }

        foreach (var fragment in choice.Delta.ToolCalls)
        {
            AppendToolCallFragment(fragment);
        }
    }

    private void AppendToolCallFragment(OpenAiToolCallDelta? fragment)
    {
        if (fragment?.Index is not { } index || index < 0 || index > toolCalls.Count)
        {
            throw InvalidToolCall();
        }

        if (index == toolCalls.Count)
        {
            if (string.IsNullOrWhiteSpace(fragment.Id))
            {
                throw InvalidToolCall();
            }

            toolCalls.Add(new OpenAiStreamingToolCallBuilder(fragment.Id));
        }

        if (!toolCalls[index].TryMerge(fragment))
        {
            throw InvalidToolCall();
        }
    }

    private AiModelException InvalidToolCall()
    {
        return OpenAiModelErrorMapper.InvalidToolCall(OpenAiModelResponseMapper.MapUsage(usage), model);
    }
}
