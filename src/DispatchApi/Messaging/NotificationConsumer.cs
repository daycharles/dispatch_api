using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DispatchApi.Messaging;

/// <summary>
/// Consumes dispatch.notifications and turns each delivery into a call to
/// NotificationHandler.
///
/// Everything here is transport: acknowledgement, prefetch, scoping and the
/// decision to retry or dead-letter. The rules about what an event means live in
/// the handler, which is why they can be tested without a broker.
/// </summary>
public sealed class NotificationConsumer : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly IRabbitMqConnection _connection;
    private readonly TopologyInitializer _topology;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MessagingOptions _options;
    private readonly ILogger<NotificationConsumer> _logger;

    public NotificationConsumer(
        IRabbitMqConnection connection,
        TopologyInitializer topology,
        IServiceScopeFactory scopeFactory,
        IOptions<MessagingOptions> options,
        ILogger<NotificationConsumer> logger)
    {
        _connection = connection;
        _topology = topology;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The client recovers a dropped connection on its own; this loop
                // is for the case it cannot, above all a broker that was never
                // reachable in the first place.
                _logger.LogError(ex, "Consumer stopped; retrying in {Delay}.", ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        // Re-declared on every (re)connect, so a broker that was rebuilt while
        // this process was running does not leave the consumer waiting on a
        // queue that no longer exists.
        await _topology.DeclareAsync(stoppingToken);

        var connection = await _connection.GetAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false),
            stoppingToken);

        // Per-consumer, not global. Without it the broker hands over the whole
        // queue at once, which defeats the delivery limit and makes a restart
        // lose far more in-flight work than it needs to.
        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: _options.PrefetchCount,
            global: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => OnDeliveryAsync(channel, delivery, stoppingToken);

        // autoAck: false. With autoAck the broker forgets the message the moment
        // it is written to the socket, so a consumer crash loses it silently.
        await channel.BasicConsumeAsync(
            queue: DispatchTopology.NotificationQueue,
            autoAck: false,
            consumerTag: _options.ClientName,
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer: consumer,
            cancellationToken: stoppingToken);

        _logger.LogInformation(
            "Consuming {Queue} with prefetch {Prefetch}.",
            DispatchTopology.NotificationQueue, _options.PrefetchCount);

        // Deliveries arrive on the consumer's dispatcher, so this task exists
        // only to keep the channel open until shutdown.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task OnDeliveryAsync(
        IChannel channel,
        BasicDeliverEventArgs delivery,
        CancellationToken stoppingToken)
    {
        var messageId = delivery.BasicProperties.MessageId;

        // Without a message id the handler cannot tell a redelivery from a new
        // event, so it cannot be processed safely. That is a producer defect, not
        // a transient failure, and retrying will not add an id.
        if (string.IsNullOrEmpty(messageId))
        {
            _logger.LogError(
                "Delivery {DeliveryTag} with routing key {RoutingKey} has no message id; dead-lettering it.",
                delivery.DeliveryTag, delivery.RoutingKey);

            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
            return;
        }

        try
        {
            // A scope per message, so each one gets its own DbContext. Sharing one
            // across deliveries would leak the change tracker between unrelated
            // units of work and make a failed message poison the next.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<NotificationHandler>();

            await handler.HandleAsync(delivery.RoutingKey, messageId, delivery.Body, stoppingToken);

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
        }
        catch (PoisonMessageException ex)
        {
            _logger.LogError(
                ex, "Message {MessageId} ({RoutingKey}) is poison; dead-lettering it.",
                messageId, delivery.RoutingKey);

            // requeue: false sends it to the DLX immediately rather than burning
            // five redeliveries on something that will never parse.
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Message {MessageId} ({RoutingKey}) failed; requeueing it.",
                messageId, delivery.RoutingKey);

            // Transient, so it is worth retrying. x-delivery-limit is what stops
            // this becoming an infinite loop if it turns out not to be.
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
        }
    }
}
