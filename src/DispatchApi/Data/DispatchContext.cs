using DispatchApi.Models;
using Microsoft.EntityFrameworkCore;

namespace DispatchApi.Data;

public class DispatchContext : DbContext
{
    public DispatchContext(DbContextOptions<DispatchContext> options) : base(options) { }

    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Assignment> Assignments => Set<Assignment>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Unit>()
            .HasIndex(u => u.CallSign)
            .IsUnique();

        b.Entity<Incident>()
            .Ignore(i => i.TimeToFirstAssignmentSeconds);

        // Queries filter on open work and on priority far more than anything
        // else, so this is the index that earns its keep.
        b.Entity<Incident>()
            .HasIndex(i => new { i.Status, i.Priority });

        b.Entity<Assignment>()
            .HasOne(a => a.Incident)
            .WithMany(i => i.Assignments)
            .HasForeignKey(a => a.IncidentId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<Assignment>()
            .HasOne(a => a.Unit)
            .WithMany(u => u.Assignments)
            .HasForeignKey(a => a.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        // One active assignment per unit per incident.
        b.Entity<Assignment>()
            .HasIndex(a => new { a.UnitId, a.IncidentId, a.ClearedAtUtc });
    }
}
