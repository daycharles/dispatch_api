using System.ComponentModel.DataAnnotations;

namespace DispatchApi.Models;

public class Incident
{
    public int Id { get; set; }

    [Required, MaxLength(64)]
    public string CallType { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? Address { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public Priority Priority { get; set; } = Priority.Priority3;

    public IncidentStatus Status { get; set; } = IncidentStatus.Open;

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public DateTimeOffset? FirstAssignedAtUtc { get; set; }

    public DateTimeOffset? ClosedAtUtc { get; set; }

    public List<Assignment> Assignments { get; set; } = new();

    /// <summary>
    /// Seconds between the call being received and the first unit being assigned.
    /// Null until a unit is assigned. This is the metric dispatch centres are
    /// measured on, so it is stored as a derived read rather than recomputed
    /// by callers.
    /// </summary>
    public double? TimeToFirstAssignmentSeconds =>
        FirstAssignedAtUtc is null
            ? null
            : (FirstAssignedAtUtc.Value - ReceivedAtUtc).TotalSeconds;
}
