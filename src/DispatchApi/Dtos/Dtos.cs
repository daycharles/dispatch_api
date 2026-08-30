using DispatchApi.Models;

namespace DispatchApi.Dtos;

public record CreateIncidentRequest(
    string CallType,
    string? Address,
    double? Latitude,
    double? Longitude,
    Priority Priority);

public record IncidentResponse(
    int Id,
    string CallType,
    string? Address,
    Priority Priority,
    IncidentStatus Status,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? FirstAssignedAtUtc,
    double? TimeToFirstAssignmentSeconds,
    IReadOnlyList<string> AssignedUnits)
{
    public static IncidentResponse From(Incident i) => new(
        i.Id,
        i.CallType,
        i.Address,
        i.Priority,
        i.Status,
        i.ReceivedAtUtc,
        i.FirstAssignedAtUtc,
        i.TimeToFirstAssignmentSeconds,
        i.Assignments
            .Where(a => a.ClearedAtUtc is null)
            .Select(a => a.Unit?.CallSign ?? $"#{a.UnitId}")
            .ToList());
}

/// <summary>
/// Raised by the message consumer, not by the request that caused it. Reading
/// these back is the end-to-end proof that a message was published, routed and
/// consumed.
/// </summary>
public record NotificationResponse(
    int Id,
    int IncidentId,
    string Trigger,
    string Message,
    DateTimeOffset RaisedAtUtc)
{
    public static NotificationResponse From(IncidentNotification n) =>
        new(n.Id, n.IncidentId, n.Trigger, n.Message, n.RaisedAtUtc);
}

public record CreateUnitRequest(string CallSign);

public record UnitResponse(int Id, string CallSign, UnitStatus Status)
{
    public static UnitResponse From(Unit u) => new(u.Id, u.CallSign, u.Status);
}

public record AssignRequest(int UnitId);

/// <summary>Result of a dispatch operation. Avoids exceptions for expected refusals.</summary>
public record DispatchResult(bool Success, string? Error)
{
    public static DispatchResult Ok() => new(true, null);
    public static DispatchResult Fail(string error) => new(false, error);
}
