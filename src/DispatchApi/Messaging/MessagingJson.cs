using System.Text.Json;

namespace DispatchApi.Messaging;

/// <summary>
/// The one serializer configuration used on both sides of the wire.
///
/// Publisher and consumer are the same process today, but the whole point of a
/// broker is that one day they will not be. A single shared static is what stops
/// the two ends drifting into a format mismatch that no unit test would catch:
/// adding a JsonStringEnumConverter here, for instance, would break every
/// message already sitting in the queue.
/// </summary>
public static class MessagingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
