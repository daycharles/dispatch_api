using System.Text.Json;
using DispatchApi.Data;
using DispatchApi.Models;
using DispatchApi.Services;

namespace DispatchApi.Messaging;

/// <summary>
/// What this service does when it receives an incident event.
///
/// Deliberately free of any RabbitMQ type: it takes a routing key, an id and a
/// body, and it either succeeds or throws. That is what makes the interesting
/// half of the consumer testable without a broker, and it is the seam where
/// PoisonMessageException tells the transport to dead-letter rather than retry.
/// </summary>
public sealed class NotificationHandler
{
    private readonly DispatchContext _db;
    private readonly IProcessedMessageStore _processed;
    private readonly IClock _clock;
    private readonly ILogger<NotificationHandler> _logger;

    public NotificationHandler(
        DispatchContext db,
        IProcessedMessageStore processed,
        IClock clock,
        ILogger<NotificationHandler> logger)
    {
        _db = db;
        _processed = processed;
        _clock = clock;
        _logger = logger;
    }

    public async Task HandleAsync(
        string routingKey,
        string messageId,
        ReadOnlyMemory<byte> body,
        CancellationToken ct)
    {
        if (await _processed.HasProcessedAsync(DispatchTopology.ConsumerName, messageId, ct))
        {
            _logger.LogDebug(
                "Message {MessageId} ({RoutingKey}) already handled; skipping.", messageId, routingKey);
            return;
        }

        var notification = Interpret(routingKey, body);

        if (notification is not null)
            _db.IncidentNotifications.Add(notification);

        // Marked handled even when there was nothing to do, so a redelivery does
        // not re-evaluate the rules against a database that has since changed.
        _processed.Stage(DispatchTopology.ConsumerName, messageId);

        // One save. The notification and the record that this message produced it
        // are in the same transaction, so the consumer can crash anywhere without
        // leaving one without the other.
        await _db.SaveChangesAsync(ct);

        if (notification is not null)
        {
            _logger.LogInformation(
                "Raised notification for incident {IncidentId} from {RoutingKey}.",
                notification.IncidentId, routingKey);
        }
    }

    /// <summary>
    /// Returns the notification the event warrants, or null when the event is one
    /// this consumer knowingly has no opinion about. Throws PoisonMessageException
    /// when the message could never be understood.
    /// </summary>
    private IncidentNotification? Interpret(string routingKey, ReadOnlyMemory<byte> body) =>
        routingKey switch
        {
            DispatchTopology.RoutingKeys.IncidentCreated => OnIncidentCreated(Parse<IncidentCreated>(body, routingKey)),
            DispatchTopology.RoutingKeys.IncidentClosed => OnIncidentClosed(Parse<IncidentClosed>(body, routingKey)),

            // The queue binds incident.*, so this consumer sees assignment traffic
            // it does not care about. Treating that as poison would dead-letter
            // perfectly good messages.
            DispatchTopology.RoutingKeys.UnitAssigned or DispatchTopology.RoutingKeys.UnitCleared => null,

            _ => throw new PoisonMessageException(
                $"No handler for routing key '{routingKey}'.")
        };

    /// <summary>
    /// Only the calls a supervisor has to see immediately. Notifying on every
    /// incident would train people to ignore the notifications.
    /// </summary>
    private IncidentNotification? OnIncidentCreated(IncidentCreated e)
    {
        if (e.Priority != Priority.Priority1)
            return null;

        var where = string.IsNullOrWhiteSpace(e.Address) ? "an unspecified location" : e.Address;

        return new IncidentNotification
        {
            IncidentId = e.IncidentId,
            Trigger = DispatchTopology.RoutingKeys.IncidentCreated,
            Message = $"Priority 1: {e.CallType} at {where}. Assign a unit immediately.",
            RaisedAtUtc = _clock.UtcNow
        };
    }

    /// <summary>
    /// A closed incident that never had a unit is the one worth reviewing, so it
    /// is flagged when the metric is missing rather than when it is slow.
    /// </summary>
    private IncidentNotification? OnIncidentClosed(IncidentClosed e)
    {
        if (e.TimeToFirstAssignmentSeconds is not null)
            return null;

        return new IncidentNotification
        {
            IncidentId = e.IncidentId,
            Trigger = DispatchTopology.RoutingKeys.IncidentClosed,
            Message = $"Incident {e.IncidentId} was closed with no unit ever assigned.",
            RaisedAtUtc = _clock.UtcNow
        };
    }

    private static T Parse<T>(ReadOnlyMemory<byte> body, string routingKey)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body.Span, MessagingJson.Options)
                ?? throw new PoisonMessageException($"Body of '{routingKey}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            // Not transient: no number of redeliveries will make this parse.
            throw new PoisonMessageException($"Body of '{routingKey}' is not valid JSON for {typeof(T).Name}.", ex);
        }
    }
}
