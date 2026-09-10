namespace IncidentCompass.Infrastructure.Investigation;

/// <summary>
/// What makes two investigation model calls the same participant of a published report: the whole
/// of the call kind, the role, the route and the provider and model that actually answered.
/// </summary>
/// <remarks>
/// The route is part of the key as well as the model, so two routes that happen to name the same
/// model stay two participants, and the model is part of the key as well as the role, so one role
/// answered by two models stays two participants.
/// </remarks>
internal readonly record struct ModelCallParticipantKey(
    string CallKind,
    string? Role,
    string RouteId,
    string Provider,
    string Model);
