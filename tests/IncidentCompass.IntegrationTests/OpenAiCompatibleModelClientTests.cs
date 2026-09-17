using System.Text.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.IntegrationTests;

public sealed class OpenAiCompatibleModelClientTests
{
    [Fact]
    public async Task CompleteAsync_RetriesTransientProviderStatus()
    {
        await using var app = CreateFakeOpenAiCompatibleServer();
        await app.StartAsync();
        var baseUrl = GetServerAddress(app);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:Provider"] = "OpenAiCompatible",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:BaseUrl"] = baseUrl,
                ["IncidentCompass:ModelGateway:OpenAiCompatible:ApiKey"] = "test-api-key",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:AllowInsecureHttpForLoopback"] = "true",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:MaxRetryAttempts"] = "1",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:RetryBaseDelayMilliseconds"] = "1"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        var modelClient = provider.GetRequiredService<IAiModelClient>();

        var response = await modelClient.CompleteAsync(
            new AiModelRequest(
                CorrelationId: "retry-test",
                Model: "retry-model",
                Messages: [new AiChatMessage(AiMessageRole.User, "hello")]),
            TestContext.Current.CancellationToken);

        Assert.Equal("retried response", response.Content);
        var attempts = app.Services.GetRequiredService<AttemptCounter>();
        Assert.Equal(2, attempts.Value);
        Assert.Collection(
            attempts.IdempotencyKeys,
            key => Assert.StartsWith("incidentcompass-", key, StringComparison.Ordinal),
            key => Assert.StartsWith("incidentcompass-", key, StringComparison.Ordinal));
        Assert.Equal(attempts.IdempotencyKeys[0], attempts.IdempotencyKeys[1]);
    }

    [Fact]
    public async Task CompleteAsync_RejectsMalformedToolArguments()
    {
        await using var app = CreateMalformedToolArgumentsOpenAiCompatibleServer();
        await app.StartAsync();
        var baseUrl = GetServerAddress(app);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:Provider"] = "OpenAiCompatible",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:BaseUrl"] = baseUrl,
                ["IncidentCompass:ModelGateway:OpenAiCompatible:ApiKey"] = "test-api-key",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:AllowInsecureHttpForLoopback"] = "true",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:MaxRetryAttempts"] = "0"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        var modelClient = provider.GetRequiredService<IAiModelClient>();

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(
                CreateRequest("tool-model"),
                TestContext.Current.CancellationToken));

        Assert.Equal(ProviderFailureKind.InvalidResponse, exception.FailureKind);
        Assert.Equal("invalid_response", exception.ErrorCode);
        Assert.Equal(new AiModelUsage(1, 2, 3), exception.Usage);
        Assert.Equal("tool-model", exception.ReturnedModel);
    }

    [Fact]
    public async Task CompleteAsync_SerializesAgenticToolResultTurn()
    {
        await using var app = CreateCapturingOpenAiCompatibleServer();
        await app.StartAsync();
        var baseUrl = GetServerAddress(app);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:Provider"] = "OpenAiCompatible",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:BaseUrl"] = baseUrl,
                ["IncidentCompass:ModelGateway:OpenAiCompatible:ApiKey"] = "test-api-key",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:AllowInsecureHttpForLoopback"] = "true",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:MaxRetryAttempts"] = "0"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        var modelClient = provider.GetRequiredService<IAiModelClient>();
        using var arguments = JsonDocument.Parse("{}");
        var toolCall = new AiToolCall(
            "call-profile-1",
            "GetCurrentUserProfile",
            "v1",
            arguments.RootElement.Clone());
        const string toolResult = """{"userId":"alice","tenantId":"tenant-a"}""";

        var response = await modelClient.CompleteAsync(
            new AiModelRequest(
                CorrelationId: "agentic-tool-turn",
                Model: "agentic-model",
                Messages:
                [
                    new AiChatMessage(AiMessageRole.System, "system prompt"),
                    new AiChatMessage(AiMessageRole.User, "Use my profile."),
                    new AiChatMessage(AiMessageRole.Assistant, "Tool proposed.", ToolCalls: [toolCall]),
                    new AiChatMessage(AiMessageRole.Tool, toolResult, ToolCallId: toolCall.Id)
                ],
                Tools:
                [
                    new AiToolDefinition(
                        "GetCurrentUserProfile",
                        "Returns the current profile.",
                        "v1",
                        JsonSerializer.SerializeToElement(new { }))
                ]),
            TestContext.Current.CancellationToken);

        Assert.Equal("final response", response.Content);
        var recorder = app.Services.GetRequiredService<RequestRecorder>();
        using var payload = JsonDocument.Parse(Assert.Single(recorder.Payloads));
        AssertNoNullProperties(payload.RootElement);
        var messages = payload.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .ToArray();
        var assistantIndex = Array.FindIndex(
            messages,
            static message => message.GetProperty("role").GetString() == "assistant" &&
                              message.TryGetProperty("tool_calls", out var toolCalls) &&
                              toolCalls.ValueKind == JsonValueKind.Array);
        Assert.True(assistantIndex >= 0);

        var assistantMessage = messages[assistantIndex];
        Assert.False(assistantMessage.TryGetProperty("tool_call_id", out _));
        var serializedToolCall = Assert.Single(assistantMessage.GetProperty("tool_calls").EnumerateArray());
        Assert.Equal(toolCall.Id, serializedToolCall.GetProperty("id").GetString());
        Assert.Equal("function", serializedToolCall.GetProperty("type").GetString());
        var function = serializedToolCall.GetProperty("function");
        Assert.Equal(toolCall.Name, function.GetProperty("name").GetString());
        Assert.Equal("{}", function.GetProperty("arguments").GetString());

        Assert.True(assistantIndex + 1 < messages.Length);
        var toolMessage = messages[assistantIndex + 1];
        Assert.Equal("tool", toolMessage.GetProperty("role").GetString());
        Assert.Equal(toolCall.Id, toolMessage.GetProperty("tool_call_id").GetString());
        Assert.Contains(toolResult, toolMessage.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.False(toolMessage.TryGetProperty("tool_calls", out _));
    }

    [Fact]
    public async Task CompleteAsync_NormalizesProviderErrorCode()
    {
        await using var app = CreateFailingOpenAiCompatibleServer();
        await app.StartAsync();
        var baseUrl = GetServerAddress(app);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:Provider"] = "OpenAiCompatible",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:BaseUrl"] = baseUrl,
                ["IncidentCompass:ModelGateway:OpenAiCompatible:ApiKey"] = "test-api-key",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:AllowInsecureHttpForLoopback"] = "true",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:MaxRetryAttempts"] = "0"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        var modelClient = provider.GetRequiredService<IAiModelClient>();

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(
                new AiModelRequest(
                    CorrelationId: "error-test",
                    Model: "error-model",
                    Messages: [new AiChatMessage(AiMessageRole.User, "hello")]),
                TestContext.Current.CancellationToken));

        Assert.Equal("invalid_request", exception.ErrorCode);
        Assert.Equal("raw_provider_code", exception.ProviderErrorCode);
        Assert.Equal(ProviderFailureKind.RejectedRequest, exception.FailureKind);
    }

    [Fact]
    public async Task CompleteAsync_NormalizesInvalidProviderConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:Provider"] = "OpenAiCompatible",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:BaseUrl"] = "not-a-valid-uri",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:ApiKey"] = "test-api-key"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        var modelClient = provider.GetRequiredService<IAiModelClient>();

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(
                new AiModelRequest(
                    CorrelationId: "config-test",
                    Model: "config-model",
                    Messages: [new AiChatMessage(AiMessageRole.User, "hello")]),
                TestContext.Current.CancellationToken));

        Assert.Equal("configuration_error", exception.ErrorCode);
        Assert.Equal("openai-compatible", exception.Provider);
        Assert.Equal(ProviderFailureKind.RejectedRequest, exception.FailureKind);
    }

    [Fact]
    public async Task CompleteAsync_RejectsHttpProviderEndpointByDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:Provider"] = "OpenAiCompatible",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:BaseUrl"] = "http://127.0.0.1:12345",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:ApiKey"] = "test-api-key"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        var modelClient = provider.GetRequiredService<IAiModelClient>();

        var exception = await Assert.ThrowsAsync<AiModelException>(() =>
            modelClient.CompleteAsync(
                new AiModelRequest(
                    CorrelationId: "config-test",
                    Model: "config-model",
                    Messages: [new AiChatMessage(AiMessageRole.User, "hello")]),
                TestContext.Current.CancellationToken));

        Assert.Equal("configuration_error", exception.ErrorCode);
        Assert.Equal("openai-compatible", exception.Provider);
        Assert.Equal(ProviderFailureKind.RejectedRequest, exception.FailureKind);
    }

    private static WebApplication CreateFakeOpenAiCompatibleServer()
    {
        var builder = LoopbackTestServer.CreateBuilder();
        builder.Services.AddSingleton<AttemptCounter>();

        var app = builder.Build();
        app.MapPost("/v1/chat/completions", async context =>
        {
            var attempts = context.RequestServices.GetRequiredService<AttemptCounter>();
            attempts.Value++;
            attempts.IdempotencyKeys.Add(context.Request.Headers["Idempotency-Key"].ToString());

            if (attempts.Value == 1)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = new
                    {
                        message = "rate limit",
                        code = "rate_limit"
                    }
                });
                return;
            }

            await context.Response.WriteAsJsonAsync(new
            {
                model = "retry-model",
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content = "retried response"
                        }
                    }
                },
                usage = new
                {
                    prompt_tokens = 1,
                    completion_tokens = 2,
                    total_tokens = 3
                }
            });
        });

        return app;
    }

    private static WebApplication CreateMalformedToolArgumentsOpenAiCompatibleServer()
    {
        var builder = LoopbackTestServer.CreateBuilder();

        var app = builder.Build();
        app.MapPost("/v1/chat/completions", async context =>
        {
            await context.Response.WriteAsJsonAsync(new
            {
                model = "tool-model",
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content = "Tool proposed.",
                            tool_calls = new[]
                            {
                                new
                                {
                                    id = "call-1",
                                    type = "function",
                                    function = new
                                    {
                                        name = "GetCurrentUserProfile",
                                        arguments = "not-json"
                                    }
                                }
                            }
                        }
                    }
                },
                usage = new
                {
                    prompt_tokens = 1,
                    completion_tokens = 2,
                    total_tokens = 3
                }
            });
        });

        return app;
    }

    private static WebApplication CreateCapturingOpenAiCompatibleServer()
    {
        var builder = LoopbackTestServer.CreateBuilder();
        builder.Services.AddSingleton<RequestRecorder>();

        var app = builder.Build();
        app.MapPost("/v1/chat/completions", async context =>
        {
            var recorder = context.RequestServices.GetRequiredService<RequestRecorder>();
            using var reader = new StreamReader(context.Request.Body);
            recorder.Payloads.Add(await reader.ReadToEndAsync());

            await context.Response.WriteAsJsonAsync(new
            {
                model = "agentic-model",
                choices = new[]
                {
                    new
                    {
                        message = new
                        {
                            content = "final response"
                        }
                    }
                },
                usage = new
                {
                    prompt_tokens = 4,
                    completion_tokens = 2,
                    total_tokens = 6
                }
            });
        });

        return app;
    }

    private static WebApplication CreateFailingOpenAiCompatibleServer()
    {
        var builder = LoopbackTestServer.CreateBuilder();

        var app = builder.Build();
        app.MapPost("/v1/chat/completions", async context =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = "raw provider detail",
                    code = "raw_provider_code"
                }
            });
        });

        return app;
    }

    private static string GetServerAddress(WebApplication app)
    {
        return LoopbackTestServer.GetAddress(app);
    }

    private static AiModelRequest CreateRequest(string model = "test-model")
    {
        return new AiModelRequest(
            CorrelationId: "provider-protocol-test",
            Model: model,
            Messages: [new AiChatMessage(AiMessageRole.User, "hello")]);
    }

    private static void AssertNoNullProperties(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Assert.NotEqual(JsonValueKind.Null, property.Value.ValueKind);
                    AssertNoNullProperties(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertNoNullProperties(item);
                }

                break;
        }
    }

    private sealed class AttemptCounter
    {
        public int Value { get; set; }

        public List<string> IdempotencyKeys { get; } = [];
    }

    private sealed class RequestRecorder
    {
        public List<string> Payloads { get; } = [];
    }
}
