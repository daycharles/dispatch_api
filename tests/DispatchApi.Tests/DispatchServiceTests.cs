using DispatchApi.Dtos;
using DispatchApi.Models;
using DispatchApi.Services;
using Xunit;

namespace DispatchApi.Tests;

public class DispatchServiceTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (DispatchService svc, FakeClock clock, Data.DispatchContext db) Build()
    {
        var (svc, clock, db, _) = BuildWithPublisher();
        return (svc, clock, db);
    }

    private static (DispatchService svc, FakeClock clock, Data.DispatchContext db, RecordingPublisher pub)
        BuildWithPublisher()
    {
        var db = TestDb.Create();
        var clock = new FakeClock(T0);
        var publisher = new RecordingPublisher();
        return (new DispatchService(db, clock, publisher), clock, db, publisher);
    }

    private static async Task<Unit> AddUnitAsync(Data.DispatchContext db, string callSign)
    {
        var unit = new Unit { CallSign = callSign };
        db.Units.Add(unit);
        await db.SaveChangesAsync();
        return unit;
    }

    private static CreateIncidentRequest Call(Priority p = Priority.Priority2) =>
        new("Traffic Stop", "100 Main St", 38.99, -80.23, p);

    [Fact]
    public async Task CreateIncident_starts_open_and_stamps_received_time()
    {
        var (svc, _, _) = Build();

        var incident = await svc.CreateIncidentAsync(Call());

        Assert.Equal(IncidentStatus.Open, incident.Status);
        Assert.Equal(T0, incident.ReceivedAtUtc);
        Assert.Null(incident.FirstAssignedAtUtc);
        Assert.Null(incident.TimeToFirstAssignmentSeconds);
    }

    [Fact]
    public async Task AssignUnit_commits_the_unit_and_the_incident()
    {
        var (svc, _, db) = Build();
        var unit = await AddUnitAsync(db, "12A");
        var incident = await svc.CreateIncidentAsync(Call());

        var result = await svc.AssignUnitAsync(incident.Id, unit.Id);

        Assert.True(result.Success);
        Assert.Equal(UnitStatus.Assigned, unit.Status);
        Assert.Equal(IncidentStatus.Assigned, incident.Status);
    }

    [Fact]
    public async Task TimeToFirstAssignment_measures_from_receipt_to_first_unit()
    {
        var (svc, clock, db) = Build();
        var unit = await AddUnitAsync(db, "12A");
        var incident = await svc.CreateIncidentAsync(Call());

        clock.Advance(TimeSpan.FromSeconds(45));
        await svc.AssignUnitAsync(incident.Id, unit.Id);

        Assert.Equal(45, incident.TimeToFirstAssignmentSeconds);
    }

    [Fact]
    public async Task Second_unit_does_not_reset_the_first_assignment_stamp()
    {
        var (svc, clock, db) = Build();
        var first = await AddUnitAsync(db, "12A");
        var second = await AddUnitAsync(db, "14B");
        var incident = await svc.CreateIncidentAsync(Call());

        clock.Advance(TimeSpan.FromSeconds(30));
        await svc.AssignUnitAsync(incident.Id, first.Id);

        clock.Advance(TimeSpan.FromMinutes(5));
        await svc.AssignUnitAsync(incident.Id, second.Id);

        Assert.Equal(30, incident.TimeToFirstAssignmentSeconds);
    }

    [Fact]
    public async Task Cannot_assign_a_unit_that_is_committed_elsewhere()
    {
        var (svc, _, db) = Build();
        var unit = await AddUnitAsync(db, "12A");
        var callA = await svc.CreateIncidentAsync(Call());
        var callB = await svc.CreateIncidentAsync(Call());

        await svc.AssignUnitAsync(callA.Id, unit.Id);
        var result = await svc.AssignUnitAsync(callB.Id, unit.Id);

        Assert.False(result.Success);
        Assert.Contains("committed", result.Error);
    }

    [Fact]
    public async Task Cannot_assign_the_same_unit_twice_to_one_incident()
    {
        var (svc, _, db) = Build();
        var unit = await AddUnitAsync(db, "12A");
        var incident = await svc.CreateIncidentAsync(Call());

        await svc.AssignUnitAsync(incident.Id, unit.Id);
        var result = await svc.AssignUnitAsync(incident.Id, unit.Id);

        Assert.False(result.Success);
        Assert.Contains("already assigned", result.Error);
    }

    [Fact]
    public async Task Cannot_assign_an_out_of_service_unit()
    {
        var (svc, _, db) = Build();
        var unit = await AddUnitAsync(db, "12A");
        unit.Status = UnitStatus.OutOfService;
        await db.SaveChangesAsync();
        var incident = await svc.CreateIncidentAsync(Call());

        var result = await svc.AssignUnitAsync(incident.Id, unit.Id);

        Assert.False(result.Success);
        Assert.Contains("out of service", result.Error);
    }

    [Fact]
    public async Task Clearing_the_last_unit_returns_the_incident_to_the_queue()
    {
        var (svc, _, db) = Build();
        var unit = await AddUnitAsync(db, "12A");
        var incident = await svc.CreateIncidentAsync(Call());
        await svc.AssignUnitAsync(incident.Id, unit.Id);

        var result = await svc.ClearUnitAsync(incident.Id, unit.Id);

        Assert.True(result.Success);
        Assert.Equal(UnitStatus.Available, unit.Status);
        Assert.Equal(IncidentStatus.Open, incident.Status);
    }

    [Fact]
    public async Task Closing_an_incident_frees_every_assigned_unit()
    {
        var (svc, _, db) = Build();
        var first = await AddUnitAsync(db, "12A");
        var second = await AddUnitAsync(db, "14B");
        var incident = await svc.CreateIncidentAsync(Call());
        await svc.AssignUnitAsync(incident.Id, first.Id);
        await svc.AssignUnitAsync(incident.Id, second.Id);

        var result = await svc.CloseIncidentAsync(incident.Id);

        Assert.True(result.Success);
        Assert.Equal(IncidentStatus.Closed, incident.Status);
        Assert.Equal(UnitStatus.Available, first.Status);
        Assert.Equal(UnitStatus.Available, second.Status);
        Assert.NotNull(incident.ClosedAtUtc);
    }

    [Fact]
    public async Task Cannot_assign_to_a_closed_incident()
    {
        var (svc, _, db) = Build();
        var unit = await AddUnitAsync(db, "12A");
        var incident = await svc.CreateIncidentAsync(Call());
        await svc.CloseIncidentAsync(incident.Id);

        var result = await svc.AssignUnitAsync(incident.Id, unit.Id);

        Assert.False(result.Success);
        Assert.Contains("closed incident", result.Error);
    }

    [Fact]
    public async Task Queue_orders_by_priority_then_by_age()
    {
        var (svc, clock, _) = Build();

        var lowPriorityOld = await svc.CreateIncidentAsync(Call(Priority.Priority3));
        clock.Advance(TimeSpan.FromMinutes(1));
        var highPriorityNew = await svc.CreateIncidentAsync(Call(Priority.Priority1));
        clock.Advance(TimeSpan.FromMinutes(1));
        var highPriorityNewer = await svc.CreateIncidentAsync(Call(Priority.Priority1));

        var queue = await svc.GetQueueAsync();

        Assert.Equal(
            new[] { highPriorityNew.Id, highPriorityNewer.Id, lowPriorityOld.Id },
            queue.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Closed_incidents_leave_the_queue()
    {
        var (svc, _, _) = Build();
        var incident = await svc.CreateIncidentAsync(Call());

        await svc.CloseIncidentAsync(incident.Id);
        var queue = await svc.GetQueueAsync();

        Assert.Empty(queue);
    }

    [Fact]
    public async Task Assigning_an_unknown_unit_fails_cleanly()
    {
        var (svc, _, _) = Build();
        var incident = await svc.CreateIncidentAsync(Call());

        var result = await svc.AssignUnitAsync(incident.Id, unitId: 9999);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }
}
