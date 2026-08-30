namespace DispatchApi.Messaging;

/// <summary>
/// What the API uses when Messaging:Enabled is false.
///
/// It logs rather than silently discarding, so that "why did nothing arrive"
/// has an answer in the same place you would look for the message itself.
/// </summary>
public sealed class NullIncidentPublisher : IIncidentPublisher
{
    private readonly ILogger<NullIncidentPublisher> _logger;

    public NullIncidentPublisher(ILogger<NullIncidentPublisher> logger) => _logger = logger;

    public Task PublishAsync(IIntegrationEvent @event, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Messaging is disabled; dropped {RoutingKey}.", @event.RoutingKey);

        return Task.CompletedTask;
    }
}
