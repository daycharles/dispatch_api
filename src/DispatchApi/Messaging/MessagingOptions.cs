namespace DispatchApi.Messaging;

/// <summary>Broker settings, bound from the "Messaging" configuration section.</summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>
    /// Lets the API run with no broker reachable, which is useful locally and in
    /// any environment where the messaging half has not been provisioned yet.
    /// When false the publisher becomes a no-op and no consumer starts, rather
    /// than the application failing to boot.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// Names the connection and the consumer in the management UI. Diagnostic
    /// only: the idempotency identity is DispatchTopology.ConsumerName, which is
    /// deliberately not configurable.
    /// </summary>
    public string ClientName { get; set; } = "dispatch-api";

    /// <summary>
    /// How many unacknowledged messages the broker will hand this consumer at
    /// once. Without it the broker pushes the whole queue and prefetch stops
    /// being a queue at all.
    /// </summary>
    public ushort PrefetchCount { get; set; } = 16;
}
