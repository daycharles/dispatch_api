using System.ComponentModel.DataAnnotations;

namespace DispatchApi.Models;

public class Unit
{
    public int Id { get; set; }

    /// <summary>Radio designator, e.g. "12A". Unique across the agency.</summary>
    [Required, MaxLength(16)]
    public string CallSign { get; set; } = string.Empty;

    public UnitStatus Status { get; set; } = UnitStatus.Available;

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public DateTimeOffset? LastLocationUpdateUtc { get; set; }

    public List<Assignment> Assignments { get; set; } = new();
}
