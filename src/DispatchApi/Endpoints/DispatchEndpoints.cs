using DispatchApi.Data;
using DispatchApi.Dtos;
using DispatchApi.Models;
using DispatchApi.Services;
using Microsoft.EntityFrameworkCore;

namespace DispatchApi.Endpoints;

public static class DispatchEndpoints
{
    public static void MapDispatchEndpoints(this WebApplication app)
    {
        var units = app.MapGroup("/api/units").WithTags("Units");

        units.MapGet("/", async (DispatchContext db, CancellationToken ct) =>
            Results.Ok((await db.Units.OrderBy(u => u.CallSign).ToListAsync(ct))
                .Select(UnitResponse.From)))
            .WithSummary("List all units");

        units.MapPost("/", async (CreateUnitRequest req, DispatchContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.CallSign))
                return Results.BadRequest("CallSign is required.");

            var exists = await db.Units.AnyAsync(u => u.CallSign == req.CallSign, ct);
            if (exists)
                return Results.Conflict($"Unit {req.CallSign} already exists.");

            var unit = new Unit { CallSign = req.CallSign.Trim() };
            db.Units.Add(unit);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/units/{unit.Id}", UnitResponse.From(unit));
        })
            .WithSummary("Register a unit");

        var incidents = app.MapGroup("/api/incidents").WithTags("Incidents");

        incidents.MapGet("/", async (IDispatchService svc, CancellationToken ct) =>
            Results.Ok((await svc.GetQueueAsync(ct)).Select(IncidentResponse.From)))
            .WithSummary("Dispatcher queue, most urgent first");

        incidents.MapGet("/{id:int}", async (int id, IDispatchService svc, CancellationToken ct) =>
        {
            var incident = await svc.GetIncidentAsync(id, ct);
            return incident is null
                ? Results.NotFound()
                : Results.Ok(IncidentResponse.From(incident));
        })
            .WithSummary("Get one incident");

        incidents.MapPost("/", async (CreateIncidentRequest req, IDispatchService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.CallType))
                return Results.BadRequest("CallType is required.");

            var incident = await svc.CreateIncidentAsync(req, ct);
            return Results.Created($"/api/incidents/{incident.Id}", IncidentResponse.From(incident));
        })
            .WithSummary("Create an incident");

        incidents.MapPost("/{id:int}/assign", async (int id, AssignRequest req, IDispatchService svc, CancellationToken ct) =>
        {
            var result = await svc.AssignUnitAsync(id, req.UnitId, ct);
            return result.Success ? Results.NoContent() : Results.BadRequest(result.Error);
        })
            .WithSummary("Assign a unit to an incident");

        incidents.MapPost("/{id:int}/clear", async (int id, AssignRequest req, IDispatchService svc, CancellationToken ct) =>
        {
            var result = await svc.ClearUnitAsync(id, req.UnitId, ct);
            return result.Success ? Results.NoContent() : Results.BadRequest(result.Error);
        })
            .WithSummary("Clear a unit from an incident");

        incidents.MapPost("/{id:int}/close", async (int id, IDispatchService svc, CancellationToken ct) =>
        {
            var result = await svc.CloseIncidentAsync(id, ct);
            return result.Success ? Results.NoContent() : Results.BadRequest(result.Error);
        })
            .WithSummary("Close an incident and clear all units");
    }
}
