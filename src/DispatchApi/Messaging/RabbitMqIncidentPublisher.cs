using System.Text.Json;
using DispatchApi.Services;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DispatchApi.Messaging;

/// <summary>
/// Publishes with confirmations, persistence and mandatory routing, which
/// together are what makes "the publish returned" mean something.
///
/// Without confirms, BasicPublishAsync only means the bytes reached the socket.
/// Without persistence, a confirmed message still dies with the broker. Without
/// mandatory, a message with a routing key nothing is bound to is discarded in
/// silence, which is the failure mode that gets found in production.
/// </summary>
public sealed class RabbitMqIncidentPublisher : IIncidentPublisher, IAsyncDisposable
{
    private readonly IRabbitMqConnection _connection;
    private readonly IClock _clock;
    private readonly ILogger<RabbitMqIncidentPublisher> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IChannel? _channel;
    private bool _disposed;

    public RabbitMqIncidentPublisher(
        IRabbitMqConnection connection,
        IClock clock,
        ILogger<RabbitMqIncidentPublisher> logger)
    {
        _connection = connection;
        _clock = clock;
        _logger = logger;
    }

    public async Task PublishAsync(IIntegrationEvent @event, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var properties = new BasicProperties
        {
            ContentType = "application/json",

            // Survives a broker restart, but only in combination with a durable
            // exchange and a durable queue. All three or none.
            DeliveryMode = DeliveryModes.Persistent,

            // The consumer's idempotency key. Generated here because the
            // publisher is the only party that knows a redelivery from a genuine
            // second event.
            MessageId = Guid.NewGuid().ToString("N"),
            Type = @event.RoutingKey,
            Timestamp = new AmqpTimestamp(_clock.UtcNow.ToUnixTimeSeconds())
        };

        // Serialized against the runtime type, not IIntegrationEvent: System.Text.Json
        // uses the static type, so serializing the interface would put an empty
        // object on the wire.
        var body = JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType(), MessagingJson.Options);

        var channel = await GetChannelAsync(ct);

        // A channel is not safe for concurrent use, and this publisher is a
        // singleton shared by every request. Serializing publishes is the cost
        // of one connection; if it ever became the bottleneck the fix is a pool
        // of channels, not a channel per publish.
        await _gate.WaitAsync(ct);
        try
        {
            await channel.BasicPublishAsync(
                exchange: DispatchTopology.Exchange,
                routingKey: @event.RoutingKey,
                mandatory: true,
                basicProperties: properties,
                body: body,
                cancellationToken: ct);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogDebug(
            "Published {RoutingKey} as {MessageId}.", @event.RoutingKey, properties.MessageId);
    }

    private async ValueTask<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        await _gate.WaitAsync(ct);
        try
        {
            if (_channel is { IsOpen: true })
                return _channel;

            if (_channel is not null)
            {
                await _channel.DisposeAsync();
                _channel = null;
            }

            var connection = await _connection.GetAsync(ct);

            // Tracking makes BasicPublishAsync await the broker's ack, so the
            // await genuinely means the broker took responsibility for it.
            var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                ct);

            // A return means the broker accepted the message and then found
            // nothing to route it to. With mandatory + confirms the publish also
            // throws, but the handler is what says which key was orphaned.
            channel.BasicReturnAsync += OnReturnedAsync;

            _channel = channel;
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task OnReturnedAsync(object sender, BasicReturnEventArgs args)
    {
        _logger.LogError(
            "Message {MessageId} with routing key {RoutingKey} was returned unrouted: {ReplyCode} {ReplyText}. "
            + "Nothing is bound to that key on {Exchange}.",
            args.BasicProperties.MessageId, args.RoutingKey, args.ReplyCode, args.ReplyText, args.Exchange);

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_channel is not null)
        {
            _channel.BasicReturnAsync -= OnReturnedAsync;
            await _channel.DisposeAsync();
        }

        _gate.Dispose();
    }
}
