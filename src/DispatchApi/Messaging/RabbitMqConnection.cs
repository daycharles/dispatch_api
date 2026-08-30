using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DispatchApi.Messaging;

/// <summary>
/// Connects lazily and once.
///
/// Lazily, because the API must be able to start before the broker does: a
/// container that will not boot when a dependency is briefly unavailable turns
/// every broker restart into an outage. Once, because the connection is shared,
/// and the semaphore is what stops a burst of first requests opening several.
/// </summary>
public sealed class RabbitMqConnection : IRabbitMqConnection
{
    private readonly MessagingOptions _options;
    private readonly ILogger<RabbitMqConnection> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;
    private bool _disposed;

    public RabbitMqConnection(IOptions<MessagingOptions> options, ILogger<RabbitMqConnection> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async ValueTask<IConnection> GetAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_connection is { IsOpen: true })
            return _connection;

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true })
                return _connection;

            // A connection that closed and did not recover is not reusable, and
            // holding on to it would mask the reconnect.
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,

                // Recovers the connection, the channels and the topology after a
                // broker restart, so a failover does not require a redeploy.
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,

                // Shows up in the management UI, which is the difference between
                // "some client is misbehaving" and knowing which one.
                ClientProvidedName = _options.ClientName
            };

            _connection = await factory.CreateConnectionAsync(ct);

            _logger.LogInformation(
                "Connected to RabbitMQ at {Host}:{Port}{VirtualHost} as {ClientName}.",
                _options.Host, _options.Port, _options.VirtualHost, _options.ClientName);

            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_connection is not null)
            await _connection.DisposeAsync();

        _gate.Dispose();
    }
}
