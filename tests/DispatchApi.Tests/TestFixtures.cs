using DispatchApi.Data;
using DispatchApi.Messaging;
using DispatchApi.Services;
using Microsoft.EntityFrameworkCore;

namespace DispatchApi.Tests;

/// <summary>Clock that returns a fixed time and can be advanced by the test.</summary>
public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset start) => UtcNow = start;

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

public static class TestDb
{
    /// <summary>
    /// A fresh in-memory store per test, so tests cannot leak state into one
    /// another and can run in parallel.
    /// </summary>
    public static DispatchContext Create()
    {
        var options = new DbContextOptionsBuilder<DispatchContext>()
            .UseInMemoryDatabase($"dispatch-{Guid.NewGuid()}")
            .Options;

        return new DispatchContext(options);
    }
}

/// <summary>
/// Captures published events instead of talking to a broker, so the rules
/// about *which* event is published and *when* are unit tested without any
/// infrastructure. Anything that needs a real broker is an integration
/// concern and is verified by running Compose.
/// </summary>
public sealed class RecordingPublisher : IIncidentPublisher
{
    public List<IIntegrationEvent> Published { get; } = new();

    public Task PublishAsync(IIntegrationEvent @event, CancellationToken ct = default)
    {
        Published.Add(@event);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> RoutingKeys =>
        Published.Select(e => e.RoutingKey).ToList();
}
