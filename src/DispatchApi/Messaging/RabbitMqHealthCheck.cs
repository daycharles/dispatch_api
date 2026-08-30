using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace DispatchApi.Messaging;

/// <summary>
/// Reports whether the broker is reachable. Tagged "ready", so it gates traffic
/// but never liveness: restarting the API does not fix a broker that is down,
/// and an orchestrator that keeps killing the pod over it only makes the outage
/// louder.
/// </summary>
public sealed class RabbitMqHealthCheck : IHealthCheck
{
    private readonly IRabbitMqConnection _connection;

    public RabbitMqHealthCheck(IRabbitMqConnection connection) => _connection = connection;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await _connection.GetAsync(cancellationToken);

            // Opening a channel, not just reading IsOpen: a connection object can
            // claim to be open while the broker refuses to do anything with it.
            await using var channel = await connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: false,
                    publisherConfirmationTrackingEnabled: false),
                cancellationToken);

            return HealthCheckResult.Healthy("Broker reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Broker unreachable.", ex);
        }
    }
}
