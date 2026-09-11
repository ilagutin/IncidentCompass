using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Notifications;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Notifications.Telegram;

public sealed partial class TelegramNotificationActionTool : IExternalActionTool, IDisposable
{
    /// <summary>
    /// The provider could not be reached and the notification was never sent. Safe to settle by
    /// proposing the action again.
    /// </summary>
    internal const string UnavailableCode = "telegram_unavailable";

    /// <summary>
    /// The request was on the wire and its answer never arrived, so whether the chat received the
    /// notification is not known. Durable, never automatically repeated, settled by a person. It is
    /// the same string the response parser already uses for the answers it cannot trust.
    /// </summary>
    internal const string OutcomeUnknownCode = "dispatch_outcome_unknown";

    public static readonly Uri Authority = new("https://api.telegram.org", UriKind.Absolute);
    private readonly TelegramOptions options;
    private readonly HttpClient client;
    private readonly ILogger logger;

    public TelegramNotificationActionTool(
        IOptions<TelegramOptions> options,
        HttpMessageHandler? handler = null,
        ILogger<TelegramNotificationActionTool>? logger = null)
    {
        this.options = options.Value;
        this.logger = logger ?? NullLogger<TelegramNotificationActionTool>.Instance;
        client = new HttpClient(handler ?? TelegramHttpMessageHandlerFactory.Create(), disposeHandler: true)
        {
            BaseAddress = Authority,
            Timeout = Timeout.InfiniteTimeSpan
        };
        AdapterBindingFingerprint = ExternalActionBinding.ComputeFingerprint(
            "telegram", LogicalTargetId, Authority.AbsoluteUri, this.options.ChatId);
    }

    public ActionCategory Category => ActionCategory.Notification;

    public string LogicalTargetId => TelegramNotificationWorkflow.LogicalTargetIdValue;

    public string AdapterBindingFingerprint { get; }

    public AiToolDefinition Definition { get; } = new(
        TelegramNotificationWorkflow.ToolIdValue,
        "Send one backend-routed Telegram incident notification.",
        "v1",
        CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["originReportId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^[0-9a-f]{32}$"
                },
                ["routeId"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("originReportId", "routeId")
        }));

    public ToolValidationResult Validate(JsonElement arguments)
    {
        if (!options.Enabled || arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Any(static property => property.Name is not ("originReportId" or "routeId")) ||
            !arguments.TryGetProperty("originReportId", out var report) || report.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(report.GetString(), "N", out var reportId) ||
            !arguments.TryGetProperty("routeId", out var route) || route.ValueKind != JsonValueKind.String ||
            !string.Equals(route.GetString(), options.RouteId, StringComparison.Ordinal))
        {
            return ToolValidationResult.Invalid("invalid_arguments", "Telegram notification arguments are invalid.");
        }

        return ToolValidationResult.Valid(JsonSerializer.SerializeToElement(new
        {
            originReportId = reportId.ToString("N"),
            routeId = options.RouteId
        }));
    }

    public ExternalActionPreparation Prepare(JsonElement sanitizedArguments) =>
        TelegramNotificationPayloadFactory.Create(new TelegramNotificationWorkflowInput(
            Guid.ParseExact(sanitizedArguments.GetProperty("originReportId").GetString()!, "N"),
            sanitizedArguments.GetProperty("routeId").GetString()!));

    public async Task<ExternalActionExecutionResult> ExecuteAsync(
        Guid actionId,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return Failure("telegram_binding_unavailable");
        }

        HttpRequestMessage request;
        try
        {
            request = TelegramNotificationRequestFactory.Create(canonicalPayload, options);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Failure("telegram_payload_invalid");
        }

        using (request)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            try
            {
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var result = await TelegramNotificationResponseParser.ParseAsync(response, deadline.Token);
                if (!result.Succeeded)
                {
                    LogProviderFailure(logger, result.FailureCode ?? "telegram_failure");
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown, not a provider fault. The dispatcher owns what a cancelled dispatch means
                // and the existing coverage pins that this still propagates.
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
            {
                var code = TransportFailureCode(exception);
                LogProviderFailure(logger, code);
                return NotDelivered(code);
            }
        }
    }

    public void Dispose() => client.Dispose();

    /// <summary>
    /// The code a transport fault gets, which is entirely a question of whether the notification may
    /// already have been delivered.
    /// </summary>
    /// <remarks>
    /// A send is a write, so this is not free to call every transport fault an unavailability the way
    /// a read adapter can. Only a failure that happened before any request byte left this process
    /// proves nothing was sent, and <see cref="HttpTransportFailureClassifier" /> is the single place
    /// that decides that for every HTTP adapter here. Everything else - a mid-flight reset, a stream
    /// fault, this adapter's own deadline expiring after the request was on the wire - may have been
    /// delivered with only its answer lost, and stays the durable in-doubt state a person settles.
    /// Before this existed none of it was mapped at all: the exception escaped into the dispatcher's
    /// catch-all, so a connection this host never opened was recorded exactly like a message that may
    /// have reached the chat.
    /// </remarks>
    private static string TransportFailureCode(Exception exception) =>
        exception is HttpRequestException transport &&
        HttpTransportFailureClassifier.IsSafePreDispatchFailure(transport)
            ? UnavailableCode
            : OutcomeUnknownCode;

    private static ExternalActionExecutionResult Failure(string code)
    {
        return Result(code, "Telegram dispatch was rejected before sending.");
    }

    private static ExternalActionExecutionResult NotDelivered(string code)
    {
        return Result(code, "Telegram did not confirm delivery.");
    }

    private static ExternalActionExecutionResult Result(string code, string summary)
    {
        var payload = new JsonObject { ["code"] = code, ["provider"] = "telegram" };
        return new ExternalActionExecutionResult(
            false,
            System.Text.Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(payload)),
            summary,
            code);
    }

    [LoggerMessage(
        EventId = 2401,
        Level = LogLevel.Warning,
        Message = "Telegram notification provider returned failure code {FailureCode}.")]
    private static partial void LogProviderFailure(ILogger logger, string failureCode);
}
