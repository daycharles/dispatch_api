namespace DispatchApi.Messaging;

/// <summary>
/// Publishes domain events. Narrow on purpose: the domain should not be able to
/// choose an exchange, a routing key or a delivery mode, because those are
/// transport decisions and the domain would get them wrong.
/// </summary>
public interface IIncidentPublisher
{
    Task PublishAsync(IIntegrationEvent @event, CancellationToken ct = default);
}
