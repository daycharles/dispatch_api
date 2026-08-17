namespace DispatchApi.Models;

/// <summary>Join between an incident and a unit, with the time it happened.</summary>
public class Assignment
{
    public int Id { get; set; }

    public int IncidentId { get; set; }
    public Incident? Incident { get; set; }

    public int UnitId { get; set; }
    public Unit? Unit { get; set; }

    public DateTimeOffset AssignedAtUtc { get; set; }

    public DateTimeOffset? ClearedAtUtc { get; set; }
}
