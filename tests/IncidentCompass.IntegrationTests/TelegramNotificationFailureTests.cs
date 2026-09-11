using System.Net;
using System.Net.Sockets;
using System.Text;
using IncidentCompass.Application.Notifications;
using IncidentCompass.Infrastructure.Notifications.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

public sealed class TelegramNotificationFailureTests
{
    public static TheoryData<HttpStatusCode, string> HttpFailures => new()
    {
        { HttpStatusCode.Unauthorized, "telegram_authentication_failed" },
        { HttpStatusCode.Forbidden, "telegram_forbidden" },
        { HttpStatusCode.TooManyRequests, "telegram_rate_limited" },
        { HttpStatusCode.BadRequest, "telegram_request_invalid" },
        { HttpStatusCode.NotFound, "telegram_not_found" },
        { HttpStatusCode.RequestTimeout, "dispatch_outcome_unknown" },
        { HttpStatusCode.InternalServerError, "dispatch_outcome_unknown" }
    };

    [Theory]
    [MemberData(nameof(HttpFailures))]
    public async Task HttpFailureIsBoundedAndSecretFree(
        HttpStatusCode status,
        string expectedCode)
    {
        const string sentinel = "provider-raw-secret-sentinel";
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"description\":\"" + sentinel + "\"}")
        };
        using var tool = new TelegramNotificationActionTool(
            Options.Create(TelegramOptionsFixture.Valid()),
            new RecordingTelegramHandler(response));
        var payload = TelegramNotificationPayloadFactory.Create(
            new TelegramNotificationWorkflowInput(Guid.NewGuid(), "telegram_ops"));

        var result = await tool.ExecuteAsync(
            Guid.NewGuid(), payload.CanonicalPayload, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.FailureCode);
        Assert.DoesNotContain(sentinel, result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, Encoding.UTF8.GetString(result.CanonicalResult), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedSuccessfulResponseIsOutcomeUnknown()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"result\":{}}")
        };
        using var tool = new TelegramNotificationActionTool(
            Options.Create(TelegramOptionsFixture.Valid()),
            new RecordingTelegramHandler(response));
        var payload = TelegramNotificationPayloadFactory.Create(
            new TelegramNotificationWorkflowInput(Guid.NewGuid(), "telegram_ops"));

        var result = await tool.ExecuteAsync(
            Guid.NewGuid(), payload.CanonicalPayload, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("dispatch_outcome_unknown", result.FailureCode);
    }

    /// <summary>
    /// A connection this host never opened is not an in-doubt dispatch. Every other HTTP adapter
    /// here turns a transport fault into a code; this one used to let the exception escape into the
    /// dispatcher's catch-all, which records the action as an unknown outcome - a durable state a
    /// person has to resolve - for a notification that provably never left the process.
    /// </summary>
    [Fact]
    public async Task AConnectionRefusedBeforeAnyByteIsSentIsUnavailableAndNotAnUnknownOutcome()
    {
        using var tool = new TelegramNotificationActionTool(
            Options.Create(TelegramOptionsFixture.Valid()),
            new ThrowingTelegramHandler(new HttpRequestException(
                HttpRequestError.ConnectionError,
                "connection refused",
                new SocketException((int)SocketError.ConnectionRefused))));
        var payload = TelegramNotificationPayloadFactory.Create(
            new TelegramNotificationWorkflowInput(Guid.NewGuid(), "telegram_ops"));

        var result = await tool.ExecuteAsync(
            Guid.NewGuid(), payload.CanonicalPayload, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(TelegramNotificationActionTool.UnavailableCode, result.FailureCode);
        Assert.NotEqual(TelegramNotificationActionTool.OutcomeUnknownCode, result.FailureCode);
    }

    /// <summary>
    /// The other half of the same rule: a send is a write, so a transport fault that cannot be shown
    /// to precede the request stays in doubt rather than being flattened into "unavailable".
    /// </summary>
    [Fact]
    public async Task ATransportFaultThatMayHaveBeenDeliveredStaysAnUnknownOutcome()
    {
        using var tool = new TelegramNotificationActionTool(
            Options.Create(TelegramOptionsFixture.Valid()),
            new ThrowingTelegramHandler(new HttpRequestException(
                HttpRequestError.ResponseEnded, "the response ended prematurely")));
        var payload = TelegramNotificationPayloadFactory.Create(
            new TelegramNotificationWorkflowInput(Guid.NewGuid(), "telegram_ops"));

        var result = await tool.ExecuteAsync(
            Guid.NewGuid(), payload.CanonicalPayload, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(TelegramNotificationActionTool.OutcomeUnknownCode, result.FailureCode);
    }

    [Fact]
    public async Task StartedRequestCancellationPropagatesForDispatcherNormalization()
    {
        using var tool = new TelegramNotificationActionTool(
            Options.Create(TelegramOptionsFixture.Valid()), new BlockingTelegramHandler());
        var payload = TelegramNotificationPayloadFactory.Create(
            new TelegramNotificationWorkflowInput(Guid.NewGuid(), "telegram_ops"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(
            Guid.NewGuid(), payload.CanonicalPayload, cancellation.Token));
    }

    [Fact]
    public async Task FailureLogContainsOnlyStableCodeAndNoHostOrProviderSentinels()
    {
        const string token = "123456:telegram_token_log_sentinel_abcdefghijklmnopqrstuvwxyz";
        const string chatId = "-100987654321";
        const string rawBody = "telegram_raw_body_log_sentinel";
        var messages = new List<string>();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new CapturingLoggerProvider(messages)));
        var options = TelegramOptionsFixture.Valid();
        options.BotToken = token;
        options.ChatId = chatId;
        using var tool = new TelegramNotificationActionTool(
            Options.Create(options),
            new RecordingTelegramHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"description\":\"" + rawBody + "\"}")
            }),
            loggerFactory.CreateLogger<TelegramNotificationActionTool>());
        var payload = TelegramNotificationPayloadFactory.Create(
            new TelegramNotificationWorkflowInput(Guid.NewGuid(), options.RouteId));

        var result = await tool.ExecuteAsync(
            Guid.NewGuid(), payload.CanonicalPayload, TestContext.Current.CancellationToken);

        var captured = string.Join("\n", messages);
        Assert.False(result.Succeeded);
        Assert.NotEmpty(messages);
        Assert.Contains("telegram_forbidden", captured, StringComparison.Ordinal);
        var errorText = result.Summary + "\n" + Encoding.UTF8.GetString(result.CanonicalResult);
        foreach (var sentinel in new[]
                 {
                     token,
                     chatId,
                     TelegramNotificationActionTool.Authority.AbsoluteUri,
                     rawBody
                 })
        {
            Assert.DoesNotContain(sentinel, captured, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, errorText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OptionsRejectPartialOrInvalidHostBindings()
    {
        var validator = new TelegramOptionsValidator();

        Assert.True(validator.Validate(null, new TelegramOptions()).Succeeded);
        Assert.False(validator.Validate(null, new TelegramOptions
        {
            Enabled = true,
            RouteId = "telegram_ops",
            ChatId = "attacker",
            BotToken = "raw-token"
        }).Succeeded);
        Assert.True(validator.Validate(null, TelegramOptionsFixture.Valid()).Succeeded);
    }

    private sealed class CapturingLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (messages)
            {
                messages.Add(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Add(exception.ToString());
                }
            }
        }
    }
}

internal sealed class ThrowingTelegramHandler(Exception failure) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(failure);
}

internal sealed class BlockingTelegramHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Unreachable.");
    }
}
