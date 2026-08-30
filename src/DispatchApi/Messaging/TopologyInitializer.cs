using RabbitMQ.Client;

namespace DispatchApi.Messaging;

/// <summary>
/// Declares the exchanges, queues and bindings before anything tries to use them.
///
/// Declaration is idempotent, so every instance doing it on boot is not waste:
/// it is what lets the stack come up in any order, and it means the topology is
/// described by the code that depends on it rather than by a runbook nobody
/// reran after the last broker rebuild.
///
/// It runs as a hosted service, registered ahead of NotificationConsumer, and
/// the consumer calls DeclareAsync again on every reconnect so that a broker
/// rebuilt while the API was running gets its topology back.
/// </summary>
public sealed class TopologyInitializer : IHostedService
{
    private readonly IRabbitMqConnection _connection;
    private readonly ILogger<TopologyInitializer> _logger;

    public TopologyInitializer(IRabbitMqConnection connection, ILogger<TopologyInitializer> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    /// <summary>
    /// Best effort. A broker that is not up yet must not stop the API from
    /// serving HTTP: that is what /health/ready is for, and the consumer retries
    /// the declaration until it succeeds.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DeclareAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex, "Could not declare the topology at startup; the consumer will retry.");
        }
    }

    public async Task DeclareAsync(CancellationToken ct)
    {
        var connection = await _connection.GetAsync(ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false),
            ct);

        await channel.ExchangeDeclareAsync(
            exchange: DispatchTopology.Exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            exchange: DispatchTopology.DeadLetterExchange,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false,
            cancellationToken: ct);

        await channel.QueueDeclareAsync(
            queue: DispatchTopology.NotificationQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: DispatchTopology.NotificationQueueArguments(),
            cancellationToken: ct);

        await channel.QueueBindAsync(
            queue: DispatchTopology.NotificationQueue,
            exchange: DispatchTopology.Exchange,
            routingKey: DispatchTopology.IncidentBinding,
            cancellationToken: ct);

        await channel.QueueDeclareAsync(
            queue: DispatchTopology.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: DispatchTopology.DeadLetterQueueArguments(),
            cancellationToken: ct);

        // Fanout ignores the routing key; the empty string is the convention.
        await channel.QueueBindAsync(
            queue: DispatchTopology.DeadLetterQueue,
            exchange: DispatchTopology.DeadLetterExchange,
            routingKey: string.Empty,
            cancellationToken: ct);

        _logger.LogInformation(
            "Declared {Exchange} -> {Queue} on {Binding}, dead-lettering to {Dlq} via {Dlx}.",
            DispatchTopology.Exchange,
            DispatchTopology.NotificationQueue,
            DispatchTopology.IncidentBinding,
            DispatchTopology.DeadLetterQueue,
            DispatchTopology.DeadLetterExchange);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
