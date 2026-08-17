namespace DispatchApi.Models;

/// <summary>Lifecycle of an incident from creation to closure.</summary>
public enum IncidentStatus
{
    Open = 0,
    Assigned = 1,
    OnScene = 2,
    Closed = 3
}

/// <summary>Availability of a responding unit.</summary>
public enum UnitStatus
{
    Available = 0,
    Assigned = 1,
    OnScene = 2,
    OutOfService = 3
}

/// <summary>
/// Call priority. Lower ordinal means higher urgency, which keeps ordering
/// comparisons readable: Priority1 &lt; Priority3.
/// </summary>
public enum Priority
{
    Priority1 = 1,
    Priority2 = 2,
    Priority3 = 3
}
