namespace DispatchApi.Messaging;

/// <summary>
/// Something that has already happened, published for anyone who cares.
///
/// The routing key lives on the event rather than at the call site so that a
/// publisher cannot send an event under the wrong key, and so that adding an
/// event type does not mean remembering to update a switch somewhere else.
/// </summary>
public interface IIntegrationEvent
{
    string RoutingKey { get; }
}
