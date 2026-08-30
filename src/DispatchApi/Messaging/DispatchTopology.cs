namespace DispatchApi.Messaging;

/// <summary>
/// The names and arguments of everything this service declares on the broker.
///
/// Kept in one place because the publisher and the consumer have to agree on
/// them exactly, and because a topology is a contract: renaming a queue is a
/// migration, not an edit.
/// </summary>
public static class DispatchTopology
{
    /// <summary>Topic exchange, so consumers choose what they want by pattern.</summary>
    public const string Exchange = "dispatch.events";

    /// <summary>
    /// Fanout, not topic: a dead-lettered message keeps its original routing
    /// key, so a topic DLX would need a binding per key and would silently drop
    /// anything it had no binding for. That is the one thing a DLX must not do.
    /// </summary>
    public const string DeadLetterExchange = "dispatch.events.dlx";

    public const string NotificationQueue = "dispatch.notifications";

    public const string DeadLetterQueue = "dispatch.notifications.dlq";

    /// <summary>
    /// One binding covering every incident event, so a new incident.* type
    /// reaches this consumer without a broker change. The consumer is expected
    /// to ignore the ones it has no opinion about.
    /// </summary>
    public const string IncidentBinding = "incident.*";

    /// <summary>
    /// Requeues before the broker gives up and dead-letters. Enough to ride out
    /// a database restart, few enough that a genuinely stuck message does not
    /// hold up the queue for long.
    /// </summary>
    public const int DeliveryLimit = 5;

    /// <summary>
    /// The identity written into ProcessedMessage.Consumer.
    ///
    /// A compile-time constant rather than configuration, on purpose: this is
    /// half of an idempotency key that is already in the database, so a typo in
    /// appsettings.json would replay every message the service has ever handled.
    /// It is also why NotificationHandler needs no options to be constructed.
    /// </summary>
    public const string ConsumerName = "dispatch.notifications";

    public static class RoutingKeys
    {
        public const string IncidentCreated = "incident.created";
        public const string UnitAssigned = "incident.assigned";
        public const string UnitCleared = "incident.cleared";
        public const string IncidentClosed = "incident.closed";
    }

    /// <summary>
    /// Quorum rather than classic: notifications have to survive the loss of the
    /// node holding them, and quorum is the replicated queue type RabbitMQ still
    /// recommends.
    /// </summary>
    public static Dictionary<string, object?> NotificationQueueArguments() => new()
    {
        ["x-queue-type"] = "quorum",
        ["x-dead-letter-exchange"] = DeadLetterExchange,
        ["x-delivery-limit"] = DeliveryLimit
    };

    /// <summary>
    /// No dead-letter exchange of its own. A DLQ that dead-letters is a loop.
    /// </summary>
    public static Dictionary<string, object?> DeadLetterQueueArguments() => new()
    {
        ["x-queue-type"] = "quorum"
    };
}
