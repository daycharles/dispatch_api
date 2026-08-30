namespace DispatchApi.Messaging;

/// <summary>
/// A message that will never succeed, no matter how many times it is redelivered:
/// a body that does not parse, or a routing key nothing knows how to interpret.
///
/// The distinction matters because it decides what the consumer does with the
/// delivery. Transient failures (the database is down) are requeued and retried;
/// these are dead-lettered immediately, because retrying them only burns the
/// delivery limit and delays every message queued behind them.
/// </summary>
public sealed class PoisonMessageException : Exception
{
    public PoisonMessageException(string message) : base(message) { }

    public PoisonMessageException(string message, Exception innerException)
        : base(message, innerException) { }
}
