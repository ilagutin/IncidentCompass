using System.Text.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The one mapping every OpenAI-compatible completion goes through, whether it arrived as one JSON
/// body or was assembled from a stream: which answers are usable, and which are refused with the
/// failure that names what really ended them.
/// </summary>
public sealed class OpenAiModelResponseMapperTests
{
    [Fact]
    public void ResponseMapper_MapsLengthWithoutUsableOutputToItsOwnFailure()
    {
        const string responseContent = """
            {
              "model": "reasoning-model",
              "choices": [
                {
                  "message": { "content": "" },
                  "finish_reason": "length"
                }
              ],
              "usage": {
                "prompt_tokens": 1596,
                "completion_tokens": 2000,
                "total_tokens": 3596,
                "completion_tokens_details": {
                  "reasoning_tokens": 1987
                }
              }
            }
            """;

        var exception = Assert.Throws<AiModelException>(() =>
            OpenAiModelResponseMapper.Map(responseContent, CreateRequest("requested-model")));

        Assert.Equal(ProviderFailureKind.OutputLimitReached, exception.FailureKind);
        Assert.Equal("provider_output_limit_reached", exception.ErrorCode);
        Assert.Equal(new AiModelUsage(1596, 2000, 3596, 1987), exception.Usage);
        Assert.Equal("reasoning-model", exception.ReturnedModel);
    }

    [Fact]
    public void ResponseMapper_MapsProviderReportedReasoningUsage()
    {
        const string responseContent = """
            {
              "model": "reasoning-model",
              "choices": [
                {
                  "message": { "content": "bounded answer" }
                }
              ],
              "usage": {
                "prompt_tokens": 21,
                "completion_tokens": 34,
                "total_tokens": 55,
                "completion_tokens_details": {
                  "reasoning_tokens": 13,
                  "reasoning_content": "must not cross the provider boundary"
                }
              }
            }
            """;

        var response = OpenAiModelResponseMapper.Map(responseContent, CreateRequest("requested-model"));

        Assert.Equal(new AiModelUsage(21, 34, 55, 13), response.Usage);
    }

    /// <summary>
    /// Half an answer is not an answer: the partial text is discarded rather than returned, and the
    /// call is still accounted, because the provider generated and charged for it. All three recognised
    /// finish reasons name the same cut: <c>length</c> is the OpenAI wire value, and <c>max_tokens</c>
    /// and <c>model_length</c> are what some OpenAI-compatible layers report for it.
    /// </summary>
    [Theory]
    [InlineData("length")]
    [InlineData("MAX_Tokens")]
    [InlineData("model_length")]
    public void ResponseMapper_RefusesAnOutputLimitFinishWithPartialContent(string finishReason)
    {
        var responseContent = $$"""
            {
              "model": "reasoning-model",
              "choices": [
                {
                  "message": { "content": "--- a/src/App.cs\n+++ b/src/App.cs\n@@ -1,2 +1,2 @@\n-old" },
                  "finish_reason": "{{finishReason}}"
                }
              ],
              "usage": {
                "prompt_tokens": 40,
                "completion_tokens": 8000,
                "total_tokens": 8040
              }
            }
            """;

        var exception = Assert.Throws<AiModelException>(() =>
            OpenAiModelResponseMapper.Map(responseContent, CreateRequest("requested-model")));

        Assert.Equal(ProviderFailureKind.OutputLimitReached, exception.FailureKind);
        Assert.Equal("provider_output_limit_reached", exception.ErrorCode);
        Assert.Equal(new AiModelUsage(40, 8000, 8040), exception.Usage);
        Assert.Equal("reasoning-model", exception.ReturnedModel);
    }

    /// <summary>
    /// Only those three finish reasons are read as a cut. A completion a provider ended for another
    /// reason keeps whatever the mapper already did with it, which is to return it as a completion.
    /// </summary>
    [Fact]
    public void ResponseMapper_DoesNotReadAContentFilterFinishAsTheOutputLimit()
    {
        const string responseContent = """
            {
              "model": "filtered-model",
              "choices": [
                {
                  "message": { "content": "as far as it got" },
                  "finish_reason": "content_filter"
                }
              ]
            }
            """;

        var response = OpenAiModelResponseMapper.Map(responseContent, CreateRequest("requested-model"));

        Assert.Equal("as far as it got", response.Content);
        Assert.Equal("filtered-model", response.Model);
    }

    /// <summary>
    /// The ceiling cut through the arguments object, so the call is the output limit it is rather than
    /// the malformed tool call it looks like.
    /// </summary>
    [Fact]
    public void ResponseMapper_RefusesLengthWithTruncatedToolCallArguments()
    {
        const string responseContent = """
            {
              "model": "tool-model",
              "choices": [
                {
                  "message": {
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call-1",
                        "type": "function",
                        "function": { "name": "memory_search", "arguments": "{\"query\":\"time" }
                      }
                    ]
                  },
                  "finish_reason": "length"
                }
              ],
              "usage": {
                "prompt_tokens": 11,
                "completion_tokens": 7,
                "total_tokens": 18
              }
            }
            """;

        var exception = Assert.Throws<AiModelException>(() =>
            OpenAiModelResponseMapper.Map(responseContent, CreateRequest("requested-model")));

        Assert.Equal(ProviderFailureKind.OutputLimitReached, exception.FailureKind);
        Assert.Equal("provider_output_limit_reached", exception.ErrorCode);
        Assert.Equal(new AiModelUsage(11, 7, 18), exception.Usage);
        Assert.Equal("tool-model", exception.ReturnedModel);
    }

    /// <summary>
    /// A length finish is refused even when what arrived happens to parse: the provider said the answer
    /// was cut off, and a call that parses is not evidence that it is the whole answer.
    /// </summary>
    [Fact]
    public void ResponseMapper_RefusesLengthWithAWellFormedToolCall()
    {
        const string responseContent = """
            {
              "model": "tool-model",
              "choices": [
                {
                  "message": {
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call-1",
                        "type": "function",
                        "function": { "name": "memory_search", "arguments": "{\"query\":\"timeout\"}" }
                      }
                    ]
                  },
                  "finish_reason": "length"
                }
              ]
            }
            """;

        var exception = Assert.Throws<AiModelException>(() =>
            OpenAiModelResponseMapper.Map(responseContent, CreateRequest("requested-model")));

        Assert.Equal(ProviderFailureKind.OutputLimitReached, exception.FailureKind);
        Assert.Equal("provider_output_limit_reached", exception.ErrorCode);
        Assert.Null(exception.Usage);
        Assert.Equal("tool-model", exception.ReturnedModel);
    }

    [Fact]
    public void ResponseMapper_AcceptsStopWithContentAndAWellFormedToolCall()
    {
        const string responseContent = """
            {
              "model": "tool-model",
              "choices": [
                {
                  "message": {
                    "content": "Looking that up.",
                    "tool_calls": [
                      {
                        "id": "call-1",
                        "type": "function",
                        "function": { "name": "memory_search", "arguments": "{\"query\":\"timeout\"}" }
                      }
                    ]
                  },
                  "finish_reason": "stop"
                }
              ]
            }
            """;

        var response = OpenAiModelResponseMapper.Map(responseContent, CreateRequest("requested-model"));

        Assert.Equal("Looking that up.", response.Content);
        Assert.Equal("tool-model", response.Model);
        var toolCall = Assert.Single(response.ProposedToolCalls ?? []);
        Assert.Equal("memory_search", toolCall.Name);
        Assert.Equal("timeout", toolCall.Arguments.GetProperty("query").GetString());
    }

    [Fact]
    public void ResponseMapper_MapsGenuinelyEmptyResponseToInvalidResponse()
    {
        const string responseContent = """
            {
              "model": "empty-model",
              "choices": [],
              "usage": {
                "prompt_tokens": 7,
                "completion_tokens": 0,
                "total_tokens": 7
              }
            }
            """;

        var exception = Assert.Throws<AiModelException>(() =>
            OpenAiModelResponseMapper.Map(responseContent, CreateRequest("requested-model")));

        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal("empty_response", exception.ErrorCode);
        Assert.Equal(new AiModelUsage(7, 0, 7), exception.Usage);
        Assert.Equal("empty-model", exception.ReturnedModel);
    }

    [Fact]
    public void ResponseMapper_RejectsToolCallWithoutProviderId()
    {
        const string responseContent = """
            {
              "model": "tool-model",
              "choices": [
                {
                  "message": {
                    "content": null,
                    "tool_calls": [
                      {
                        "type": "function",
                        "function": { "name": "memory_search", "arguments": "{}" }
                      }
                    ]
                  }
                }
              ]
            }
            """;

        var exception = Assert.Throws<AiModelException>(() =>
            OpenAiModelResponseMapper.Map(responseContent, CreateRequest("tool-model")));

        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal("invalid_response", exception.ErrorCode);
    }

    [Fact]
    public void ResponseMapper_AcceptsValidToolCall()
    {
        const string responseContent = """
            {
              "model": "tool-model",
              "choices": [
                {
                  "message": {
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call-1",
                        "type": "function",
                        "function": { "name": "memory_search", "arguments": "{\"query\":\"timeout\"}" }
                      }
                    ]
                  }
                }
              ]
            }
            """;

        var response = OpenAiModelResponseMapper.Map(responseContent, CreateRequest("tool-model"));

        var toolCall = Assert.Single(response.ProposedToolCalls ?? []);
        Assert.Equal("call-1", toolCall.Id);
        Assert.Equal("memory_search", toolCall.Name);
        Assert.Equal("timeout", toolCall.Arguments.GetProperty("query").GetString());
    }

    [Theory]
    [InlineData("{\"query\":\"plain\"}", "plain")]
    [InlineData("```\n{\"query\":\"unmarked\"}\n```", "unmarked")]
    [InlineData("```json\r\n{\"query\":\"marked\"}\r\n```", "marked")]
    [InlineData("~~~JSON\n{\"query\":\"tilde-marked\"}\n~~~", "tilde-marked")]
    public void ResponseMapper_AcceptsPlainOrSingleFencedObjectToolArguments(
        string arguments,
        string expectedQuery)
    {
        var response = OpenAiModelResponseMapper.Map(
            ToolCallResponse(arguments),
            CreateRequest("tool-model"));

        var toolCall = Assert.Single(response.ProposedToolCalls ?? []);
        Assert.Equal(expectedQuery, toolCall.Arguments.GetProperty("query").GetString());
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"{\\\"query\\\":\\\"string-wrapped\\\"}\"")]
    [InlineData("Use this object:\n```json\n{\"query\":\"mixed\"}\n```")]
    [InlineData("```json\n```json\n{\"query\":\"nested\"}\n```\n```")]
    [InlineData("```json\n{\"query\":\"first\"}\n```\n```json\n{\"query\":\"second\"}\n```")]
    [InlineData("```json\n{\"query\":\"mismatched\"}\n~~~")]
    public void ResponseMapper_RejectsAnythingOtherThanOneObjectWithOptionalOuterFence(string arguments)
    {
        var exception = Assert.Throws<AiModelException>(() =>
            OpenAiModelResponseMapper.Map(
                ToolCallResponse(arguments),
                CreateRequest("requested-model")));

        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal("invalid_response", exception.ErrorCode);
        Assert.Equal(new AiModelUsage(11, 7, 18), exception.Usage);
        Assert.Equal("tool-model", exception.ReturnedModel);
    }

    private static AiModelRequest CreateRequest(string model = "test-model")
    {
        return new AiModelRequest(
            CorrelationId: "provider-protocol-test",
            Model: model,
            Messages: [new AiChatMessage(AiMessageRole.User, "hello")]);
    }

    private static string ToolCallResponse(string arguments)
    {
        return JsonSerializer.Serialize(new
        {
            model = "tool-model",
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content = (string?)null,
                        tool_calls = new[]
                        {
                            new
                            {
                                id = "call-1",
                                type = "function",
                                function = new { name = "memory_search", arguments }
                            }
                        }
                    }
                }
            },
            usage = new { prompt_tokens = 11, completion_tokens = 7, total_tokens = 18 }
        });
    }
}
