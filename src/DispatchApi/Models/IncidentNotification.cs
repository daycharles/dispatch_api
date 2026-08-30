using System.ComponentModel.DataAnnotations;

namespace DispatchApi.Models;

/// <summary>
/// The visible output of the consumer: a notification raised in response to an
/// event, rather than inline in the request that caused it.
///
/// This is the point of the exchange. Deciding who to notify does not belong
/// in the HTTP call that creates an incident — it is slower than the caller
/// cares about, it will grow more rules over time, and it must not be able to
/// fail the incident.
/// </summary>
public class IncidentNotification
{
    public int Id { get; set; }

    public int IncidentId { get; set; }

    [MaxLength(64)]
    public string Trigger { get; set; } = string.Empty;

    [MaxLength(512)]
    public string Message { get; set; } = string.Empty;

    public DateTimeOffset RaisedAtUtc { get; set; }
}
