using IncidentCompass.Api;
using IncidentCompass.Application.Core.Exceptions;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// exercises <see cref="ApiExceptionHandler" /> directly (it is internal, visible here
/// via InternalsVisibleTo) so the logging and response-body behavior can be asserted independently
/// of any specific route.
/// </summary>
public sealed class ApiExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_GenericValidationException_ReturnsAuthoredDetailWithoutExceptionMessage()
    {
        var (handler, log) = CreateHandler();
        const string rawMessage = "Raw provider-shaped validation text that must never reach a client.";
        var exception = new ModelRequestValidationException(rawMessage);
        var httpContext = CreateHttpContext();

        var handled = await handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);
        var body = await ReadBodyAsync(httpContext);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
        Assert.Contains("\"errorCode\":\"request_validation_failed\"", body, StringComparison.Ordinal);
        Assert.Contains("The request failed validation.", body, StringComparison.Ordinal);
        Assert.DoesNotContain(rawMessage, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_DomainException_LogsTheExceptionItWasGivenWithCorrelationIdAndCode()
    {
        var (handler, log) = CreateHandler();
        const string rawMessage = "Invariant X was violated by internal state Y.";
        var exception = new InvariantViolationException(rawMessage);
        var httpContext = CreateHttpContext();
        httpContext.TraceIdentifier = "trace-domain-123";

        var handled = await handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);
        var body = await ReadBodyAsync(httpContext);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(4001, entry.EventId.Id);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Contains("trace-domain-123", entry.Message, StringComparison.Ordinal);
        Assert.Contains("internal_domain_violation", entry.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(rawMessage, body, StringComparison.Ordinal);
        Assert.Contains("\"errorCode\":\"internal_domain_violation\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_NotFoundException_LogsCorrelationIdAndResolvedCode()
    {
        var (handler, log) = CreateHandler();
        var exception = new NotFoundException(
            "Fault 'ignored-internal-id' was not found.",
            "fault_not_found",
            "The requested fault does not exist.");
        var httpContext = CreateHttpContext();
        httpContext.TraceIdentifier = "trace-notfound-456";

        await handler.TryHandleAsync(httpContext, exception, TestContext.Current.CancellationToken);
        var body = await ReadBodyAsync(httpContext);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(4002, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Contains("trace-notfound-456", entry.Message, StringComparison.Ordinal);
        Assert.Contains("fault_not_found", entry.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("ignored-internal-id", body, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found", body, StringComparison.Ordinal);
    }

    private static (ApiExceptionHandler Handler, CapturingLogger Log) CreateHandler()
    {
        var log = new CapturingLogger();
        return (new ApiExceptionHandler(log), log);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        httpContext.Response.Body = new MemoryStream();
        return httpContext;
    }

    private static async Task<string> ReadBodyAsync(HttpContext httpContext)
    {
        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(httpContext.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private sealed record CapturedLogEntry(EventId EventId, LogLevel Level, Exception? Exception, string Message);

    private sealed class CapturingLogger : ILogger<ApiExceptionHandler>
    {
        public List<CapturedLogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new CapturedLogEntry(eventId, logLevel, exception, formatter(state, exception)));
        }
    }
}
