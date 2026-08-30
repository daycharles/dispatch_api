using RabbitMQ.Client;

namespace DispatchApi.Messaging;

/// <summary>
/// One AMQP connection for the whole process, shared by the publisher, the
/// consumer and the health check.
///
/// A connection is a TCP socket with a heartbeat; channels are the cheap thing.
/// Opening one per publish is the classic way to exhaust a broker's file
/// descriptors under load.
/// </summary>
public interface IRabbitMqConnection : IAsyncDisposable
{
    ValueTask<IConnection> GetAsync(CancellationToken ct = default);
}
