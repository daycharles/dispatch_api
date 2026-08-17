using DispatchApi.Data;
using DispatchApi.Endpoints;
using DispatchApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Dispatch")
    ?? throw new InvalidOperationException(
        "Connection string 'ConnectionStrings:Dispatch' is not configured.");

builder.Services.AddDbContext<DispatchContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<IDispatchService, DispatchService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

var app = builder.Build();

// Dev convenience only. The documented next step is EF Core migrations
// (`dotnet ef migrations add Initial`) so schema changes are versioned.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<DispatchContext>();
    db.Database.EnsureCreated();
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health");
app.MapDispatchEndpoints();

app.MapGet("/", () => Results.Redirect("/swagger"))
    .ExcludeFromDescription();

app.Run();
