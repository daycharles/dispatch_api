using DispatchApi.Data;
using DispatchApi.Dtos;
using DispatchApi.Messaging;
using DispatchApi.Models;
using Microsoft.EntityFrameworkCore;

namespace DispatchApi.Services;

public interface IDispatchService
{
    Task<Incident> CreateIncidentAsync(CreateIncidentRequest request, CancellationToken ct = default);
    Task<Incident?> GetIncidentAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<Incident>> GetQueueAsync(CancellationToken ct = default);
    Task<DispatchResult> AssignUnitAsync(int incidentId, int unitId, CancellationToken ct = default);
    Task<DispatchResult> ClearUnitAsync(int incidentId, int unitId, CancellationToken ct = default);
    Task<DispatchResult> CloseIncidentAsync(int incidentId, CancellationToken ct = default);
}

public class DispatchService : IDispatchService
{
    private readonly DispatchContext _db;
    private readonly IClock _clock;
    private readonly IIncidentPublisher _publisher;

    public DispatchService(DispatchContext db, IClock clock, IIncidentPublisher publisher)
    {
        _db = db;
        _clock = clock;
        _publisher = publisher;
    }

    public async Task<Incident> CreateIncidentAsync(CreateIncidentRequest request, CancellationToken ct = default)
    {
        var incident = new Incident
        {
            CallType = request.CallType,
            Address = request.Address,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Priority = request.Priority,
            Status = IncidentStatus.Open,
            ReceivedAtUtc = _clock.UtcNow
        };

        _db.Incidents.Add(incident);
        await _db.SaveChangesAsync(ct);

        // Published after the commit, never before: an event announcing an
        // incident that then failed to save is worse than a missing event,
        // because consumers cannot un-see it.
        await _publisher.PublishAsync(new IncidentCreated(
            incident.Id,
            incident.CallType,
            incident.Address,
            incident.Priority,
            incident.ReceivedAtUtc), ct);

        return incident;
    }

    public Task<Incident?> GetIncidentAsync(int id, CancellationToken ct = default) =>
        _db.Incidents
            .Include(i => i.Assignments)
                .ThenInclude(a => a.Unit)
            .FirstOrDefaultAsync(i => i.Id == id, ct);

    /// <summary>
    /// The dispatcher's working queue: everything not yet closed, most urgent
    /// first, then oldest first within a priority. Sorting on the server rather
    /// than the client so every consumer sees the same order.
    /// </summary>
    public async Task<IReadOnlyList<Incident>> GetQueueAsync(CancellationToken ct = default) =>
        await _db.Incidents
            .Include(i => i.Assignments)
                .ThenInclude(a => a.Unit)
            .Where(i => i.Status != IncidentStatus.Closed)
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.ReceivedAtUtc)
            .ToListAsync(ct);

    public async Task<DispatchResult> AssignUnitAsync(int incidentId, int unitId, CancellationToken ct = default)
    {
        var incident = await _db.Incidents
            .Include(i => i.Assignments)
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct);

        if (incident is null)
            return DispatchResult.Fail($"Incident {incidentId} not found.");

        if (incident.Status == IncidentStatus.Closed)
            return DispatchResult.Fail("Cannot assign a unit to a closed incident.");

        var unit = await _db.Units.FirstOrDefaultAsync(u => u.Id == unitId, ct);

        if (unit is null)
            return DispatchResult.Fail($"Unit {unitId} not found.");

        if (unit.Status == UnitStatus.OutOfService)
            return DispatchResult.Fail($"Unit {unit.CallSign} is out of service.");

        var alreadyOnThisCall = incident.Assignments
            .Any(a => a.UnitId == unitId && a.ClearedAtUtc is null);

        if (alreadyOnThisCall)
            return DispatchResult.Fail($"Unit {unit.CallSign} is already assigned to this incident.");

        if (unit.Status is UnitStatus.Assigned or UnitStatus.OnScene)
            return DispatchResult.Fail($"Unit {unit.CallSign} is committed to another incident.");

        var now = _clock.UtcNow;
        var isFirstUnit = incident.FirstAssignedAtUtc is null;

        // Added through the navigation property, not _db.Assignments, so the
        // in-memory graph is correct immediately and the duplicate-assignment
        // check above sees it on the next call without a reload.
        incident.Assignments.Add(new Assignment
        {
            IncidentId = incidentId,
            UnitId = unitId,
            Unit = unit,
            AssignedAtUtc = now
        });

        unit.Status = UnitStatus.Assigned;
        incident.Status = IncidentStatus.Assigned;

        // Only stamped once, so the response-time metric measures time to the
        // FIRST unit and is not reset by later units joining the call.
        incident.FirstAssignedAtUtc ??= now;

        await _db.SaveChangesAsync(ct);

        await _publisher.PublishAsync(new UnitAssigned(
            incidentId, unitId, unit.CallSign, now, isFirstUnit), ct);

        return DispatchResult.Ok();
    }

    public async Task<DispatchResult> ClearUnitAsync(int incidentId, int unitId, CancellationToken ct = default)
    {
        var assignment = await _db.Assignments
            .Include(a => a.Unit)
            .FirstOrDefaultAsync(
                a => a.IncidentId == incidentId && a.UnitId == unitId && a.ClearedAtUtc == null,
                ct);

        if (assignment is null)
            return DispatchResult.Fail("No active assignment found for that unit on that incident.");

        var clearedAt = _clock.UtcNow;
        assignment.ClearedAtUtc = clearedAt;

        if (assignment.Unit is not null)
            assignment.Unit.Status = UnitStatus.Available;

        var incident = await _db.Incidents
            .Include(i => i.Assignments)
            .FirstAsync(i => i.Id == incidentId, ct);

        // If that was the last unit and the call is still open, it drops back
        // into the queue rather than silently looking handled.
        var stillCommitted = incident.Assignments.Any(a => a.ClearedAtUtc is null);
        if (!stillCommitted && incident.Status != IncidentStatus.Closed)
            incident.Status = IncidentStatus.Open;

        await _db.SaveChangesAsync(ct);

        await _publisher.PublishAsync(new UnitCleared(
            incidentId, unitId, assignment.Unit?.CallSign ?? $"#{unitId}", clearedAt), ct);

        return DispatchResult.Ok();
    }

    public async Task<DispatchResult> CloseIncidentAsync(int incidentId, CancellationToken ct = default)
    {
        var incident = await _db.Incidents
            .Include(i => i.Assignments)
                .ThenInclude(a => a.Unit)
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct);

        if (incident is null)
            return DispatchResult.Fail($"Incident {incidentId} not found.");

        if (incident.Status == IncidentStatus.Closed)
            return DispatchResult.Fail("Incident is already closed.");

        var now = _clock.UtcNow;

        foreach (var assignment in incident.Assignments.Where(a => a.ClearedAtUtc is null))
        {
            assignment.ClearedAtUtc = now;
            if (assignment.Unit is not null)
                assignment.Unit.Status = UnitStatus.Available;
        }

        incident.Status = IncidentStatus.Closed;
        incident.ClosedAtUtc = now;

        await _db.SaveChangesAsync(ct);

        await _publisher.PublishAsync(new IncidentClosed(
            incidentId, now, incident.TimeToFirstAssignmentSeconds), ct);

        return DispatchResult.Ok();
    }
}
