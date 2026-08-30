using System.ComponentModel.DataAnnotations;

namespace DispatchApi.Models;

/// <summary>
/// A message this consumer has already handled.
///
/// RabbitMQ guarantees at-least-once delivery, not exactly-once. A consumer
/// that crashes after doing its work but before acknowledging will see the
/// same message again, and so will one that is redelivered after a broker
/// failover. Exactly-once is achieved on the consumer side, by making
/// processing idempotent — not by the broker.
///
/// Keyed by consumer as well as message id so that adding a second consumer
/// later does not make it skip everything the first one has already seen.
/// </summary>
public class ProcessedMessage
{
    [MaxLength(64)]
    public string Consumer { get; set; } = string.Empty;

    [MaxLength(64)]
    public string MessageId { get; set; } = string.Empty;

    public DateTimeOffset ProcessedAtUtc { get; set; }
}
