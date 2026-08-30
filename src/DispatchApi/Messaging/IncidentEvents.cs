using System.Text.Json.Serialization;
using DispatchApi.Models;

namespace DispatchApi.Messaging;

/// <summary>
/// The four things that happen to an incident, expressed as facts rather than
/// commands: past tense, immutable, and carrying enough detail that a consumer
/// does not have to call back into this service to act on them.
///
/// RoutingKey is ignored by the serializer because it is envelope, not payload.
/// It already travels as the AMQP routing key and as the message type header.
/// </summary>
public sealed record IncidentCreated(
    int IncidentId,
    string CallType,
    string? Address,
    Priority Priority,
    DateTimeOffset ReceivedAtUtc) : IIntegrationEvent
{
    [JsonIgnore]
    public string RoutingKey => DispatchTopology.RoutingKeys.IncidentCreated;
}

/// <summary>
/// IsFirstUnit is decided by the publisher, at the moment it was true, rather
/// than left for each consumer to work out. Only the writer can know it without
/// racing another assignment.
/// </summary>
public sealed record UnitAssigned(
    int IncidentId,
    int UnitId,
    string CallSign,
    DateTimeOffset AssignedAtUtc,
    bool IsFirstUnit) : IIntegrationEvent
{
    [JsonIgnore]
    public string RoutingKey => DispatchTopology.RoutingKeys.UnitAssigned;
}

public sealed record UnitCleared(
    int IncidentId,
    int UnitId,
    string CallSign,
    DateTimeOffset ClearedAtUtc) : IIntegrationEvent
{
    [JsonIgnore]
    public string RoutingKey => DispatchTopology.RoutingKeys.UnitCleared;
}

/// <summary>
/// A null TimeToFirstAssignmentSeconds means no unit was ever assigned, which is
/// the case worth raising a notification about.
/// </summary>
public sealed record IncidentClosed(
    int IncidentId,
    DateTimeOffset ClosedAtUtc,
    double? TimeToFirstAssignmentSeconds) : IIntegrationEvent
{
    [JsonIgnore]
    public string RoutingKey => DispatchTopology.RoutingKeys.IncidentClosed;
}
