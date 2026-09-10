using System.Text.Json.Serialization;

namespace IncidentCompass.Application.Investigation.Reports;

/// <summary>
/// One distinct model that answered during the job attempt that produced a published report.
/// </summary>
/// <remarks>
/// <para>
/// An investigation is not one model call. The orchestrator takes one or more turns, every
/// delegated role takes at least one turn of its own, and a role names its own route, so a single
/// attempt can be answered by several routes, providers and models. A published report therefore
/// carries a list of these rather than one model name: naming only the route that emitted
/// <c>publish_report</c> would hide that a worker role ran somewhere else, and that is exactly the
/// kind of claim this project exists not to make.
/// </para>
/// <para>
/// An element is keyed by the whole of <see cref="CallKind"/>, <see cref="Role"/>,
/// <see cref="RouteId"/>, <see cref="Provider"/> and <see cref="Model"/>, so one role answered by
/// two models is two elements and the claim survives a later change that lets a role run on more
/// than one route. <see cref="Provider"/> and <see cref="Model"/> are what the provider reported
/// as having answered, not what the route asked for, and none of it can be set from model output.
/// </para>
/// <para>
/// The JSON property names and order are a persisted contract as well as a public response shape:
/// this is the element type of the <c>triage_reports.model_provenance</c> column and of
/// <c>modelProvenance</c> on the report details response, so they are pinned with explicit
/// attributes rather than left to member-declaration order.
/// </para>
/// </remarks>
/// <param name="CallKind">The model-call kind that this model answered: <c>orchestrator</c> or <c>worker</c>.</param>
/// <param name="Role">The worker role that this model answered for, or <see langword="null"/> for orchestrator calls.</param>
/// <param name="RouteId">The configured route whose call this model answered.</param>
/// <param name="Provider">The adapter identifier that reported the answer.</param>
/// <param name="Model">The provider model name that reported the answer.</param>
/// <param name="CallCount">How many calls in the attempt this exact combination answered.</param>
public sealed record TriageReportModelParticipant(
    [property: JsonPropertyName("callKind"), JsonPropertyOrder(0)] string CallKind,
    [property: JsonPropertyName("role"), JsonPropertyOrder(1)] string? Role,
    [property: JsonPropertyName("routeId"), JsonPropertyOrder(2)] string RouteId,
    [property: JsonPropertyName("provider"), JsonPropertyOrder(3)] string Provider,
    [property: JsonPropertyName("model"), JsonPropertyOrder(4)] string Model,
    [property: JsonPropertyName("callCount"), JsonPropertyOrder(5)] int CallCount);
