using System.Text;
using System.Text.Json;
using DispatchApi.Data;
using DispatchApi.Dtos;
using DispatchApi.Messaging;
using DispatchApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DispatchApi.Tests;

/// <summary>
/// Covers the two halves of the messaging path that can be tested without a
/// broker: which events the domain publishes, and how the consumer's handler
/// behaves when it gets them. What is left over — that RabbitMQ actually
/// routes, persists and dead-letters — is the broker's job, not this code's,
/// and is verified by running Compose.
/// </summary>
public class MessagingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static (Services.DispatchService svc, DispatchContext db, RecordingPublisher pub) Build()
    {
        var db = TestDb.Create();
        var publisher = new RecordingPublisher();
        return (new Services.DispatchService(db, new FakeClock(T0), publisher), db, publisher);
    }

    private static NotificationHandler Handler(DispatchContext db)
    {
        var clock = new FakeClock(T0);
        return new NotificationHandler(
            db,
            new EfProcessedMessageStore(db, clock),
            clock,
            NullLogger<NotificationHandler>.Instance);
    }

    private static ReadOnlyMemory<byte> Body<T>(T payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, Json);

    // ---------- what the domain publishes ----------

    [Fact]
    public async Task Creating_an_incident_publishes_incident_created()
    {
        var (svc, _, pub) = Build();

        await svc.CreateIncidentAsync(new CreateIncidentRequest(
            "Structure Fire", "1 Main St", null, null, Priority.Priority1));

        Assert.Equal(new[] { "incident.created" }, pub.RoutingKeys);
    }

    [Fact]
    public async Task The_full_lifecycle_publishes_one_event_per_transition()
    {
        var (svc, db, pub) = Build();
        var unit = new Unit { CallSign = "12A" };
        db.Units.Add(unit);
        await db.SaveChangesAsync();

        var incident = await svc.CreateIncidentAsync(new CreateIncidentRequest(
            "Traffic Stop", "100 Main St", null, null, Priority.Priority2));

        await svc.AssignUnitAsync(incident.Id, unit.Id);
        await svc.ClearUnitAsync(incident.Id, unit.Id);
        await svc.CloseIncidentAsync(incident.Id);

        Assert.Equal(
            new[] { "incident.created", "incident.assigned", "incident.cleared", "incident.closed" },
            pub.RoutingKeys);
    }

    [Fact]
    public async Task A_refused_assignment_publishes_nothing()
    {
        var (svc, db, pub) = Build();
        var unit = new Unit { CallSign = "12A", Status = UnitStatus.OutOfService };
        db.Units.Add(unit);
        await db.SaveChangesAsync();

        var incident = await svc.CreateIncidentAsync(new CreateIncidentRequest(
            "Traffic Stop", null, null, null, Priority.Priority3));

        var result = await svc.AssignUnitAsync(incident.Id, unit.Id);

        Assert.False(result.Success);
        // Only the creation. An event must describe something that happened.
        Assert.Equal(new[] { "incident.created" }, pub.RoutingKeys);
    }

    [Fact]
    public async Task Only_the_first_unit_is_flagged_as_first()
    {
        var (svc, db, pub) = Build();
        db.Units.AddRange(new Unit { CallSign = "12A" }, new Unit { CallSign = "12B" });
        await db.SaveChangesAsync();
        var units = await db.Units.OrderBy(u => u.CallSign).ToListAsync();

        var incident = await svc.CreateIncidentAsync(new CreateIncidentRequest(
            "Structure Fire", null, null, null, Priority.Priority1));

        await svc.AssignUnitAsync(incident.Id, units[0].Id);
        await svc.AssignUnitAsync(incident.Id, units[1].Id);

        var assigned = pub.Published.OfType<UnitAssigned>().ToList();
        Assert.True(assigned[0].IsFirstUnit);
        Assert.False(assigned[1].IsFirstUnit);
    }

    // ---------- how the consumer handles them ----------

    [Fact]
    public async Task A_priority_one_incident_raises_a_notification()
    {
        var db = TestDb.Create();

        await Handler(db).HandleAsync(
            DispatchTopology.RoutingKeys.IncidentCreated,
            "msg-1",
            Body(new IncidentCreated(7, "Structure Fire", "1 Main St", Priority.Priority1, T0)),
            CancellationToken.None);

        var notification = Assert.Single(await db.IncidentNotifications.ToListAsync());
        Assert.Equal(7, notification.IncidentId);
        Assert.Contains("Priority 1", notification.Message);
    }

    [Fact]
    public async Task A_lower_priority_incident_raises_nothing_but_is_still_recorded_as_handled()
    {
        var db = TestDb.Create();

        await Handler(db).HandleAsync(
            DispatchTopology.RoutingKeys.IncidentCreated,
            "msg-2",
            Body(new IncidentCreated(8, "Traffic Stop", null, Priority.Priority3, T0)),
            CancellationToken.None);

        Assert.Empty(await db.IncidentNotifications.ToListAsync());

        // Marked handled even though there was no work to do, so a redelivery
        // does not re-evaluate it.
        Assert.Single(await db.ProcessedMessages.ToListAsync());
    }

    [Fact]
    public async Task Redelivering_the_same_message_does_not_duplicate_the_notification()
    {
        var db = TestDb.Create();
        var body = Body(new IncidentCreated(9, "Structure Fire", "2 Oak St", Priority.Priority1, T0));

        // Same message id twice is exactly what at-least-once delivery looks
        // like after a consumer crashes between doing the work and acking.
        await Handler(db).HandleAsync(
            DispatchTopology.RoutingKeys.IncidentCreated, "msg-3", body, CancellationToken.None);
        await Handler(db).HandleAsync(
            DispatchTopology.RoutingKeys.IncidentCreated, "msg-3", body, CancellationToken.None);

        Assert.Single(await db.IncidentNotifications.ToListAsync());
    }

    [Fact]
    public async Task Two_different_messages_about_the_same_incident_both_land()
    {
        var db = TestDb.Create();
        var body = Body(new IncidentCreated(10, "Structure Fire", null, Priority.Priority1, T0));

        await Handler(db).HandleAsync(
            DispatchTopology.RoutingKeys.IncidentCreated, "msg-4", body, CancellationToken.None);
        await Handler(db).HandleAsync(
            DispatchTopology.RoutingKeys.IncidentCreated, "msg-5", body, CancellationToken.None);

        // Idempotency is per message, not per incident — deduping on incident
        // id would silently swallow genuine repeat events.
        Assert.Equal(2, (await db.IncidentNotifications.ToListAsync()).Count);
    }

    [Fact]
    public async Task Closing_with_no_unit_ever_assigned_is_flagged()
    {
        var db = TestDb.Create();

        await Handler(db).HandleAsync(
            DispatchTopology.RoutingKeys.IncidentClosed,
            "msg-6",
            Body(new IncidentClosed(11, T0, null)),
            CancellationToken.None);

        var notification = Assert.Single(await db.IncidentNotifications.ToListAsync());
        Assert.Contains("no unit ever assigned", notification.Message);
    }

    [Fact]
    public async Task Closing_after_a_unit_responded_is_not_flagged()
    {
        var db = TestDb.Create();

        await Handler(db).HandleAsync(
            DispatchTopology.RoutingKeys.IncidentClosed,
            "msg-7",
            Body(new IncidentClosed(12, T0, 42.0)),
            CancellationToken.None);

        Assert.Empty(await db.IncidentNotifications.ToListAsync());
    }

    [Fact]
    public async Task An_event_this_consumer_ignores_is_not_dead_lettered()
    {
        var db = TestDb.Create();

        // The queue binds incident.*, so this consumer sees assignment events
        // it has no opinion about. Treating those as poison would dead-letter
        // perfectly good traffic.
        await Handler(db).HandleAsync(
            DispatchTopology.RoutingKeys.UnitAssigned,
            "msg-8",
            Body(new UnitAssigned(13, 1, "12A", T0, true)),
            CancellationToken.None);

        Assert.Empty(await db.IncidentNotifications.ToListAsync());
    }

    [Fact]
    public async Task An_unknown_routing_key_is_poison()
    {
        var db = TestDb.Create();

        await Assert.ThrowsAsync<PoisonMessageException>(() => Handler(db).HandleAsync(
            "incident.teleported", "msg-9", Body(new { IncidentId = 14 }), CancellationToken.None));
    }

    [Fact]
    public async Task A_malformed_body_is_poison()
    {
        var db = TestDb.Create();
        var garbage = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{ not json"));

        // Poison, not transient: no number of redeliveries will make this
        // parse, so it belongs in the dead-letter queue immediately.
        await Assert.ThrowsAsync<PoisonMessageException>(() => Handler(db).HandleAsync(
            DispatchTopology.RoutingKeys.IncidentCreated, "msg-10", garbage, CancellationToken.None));
    }
}

