using DispatchApi.Data;
using DispatchApi.Endpoints;
using DispatchApi.Messaging;
using DispatchApi.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Dispatch")
    ?? throw new InvalidOperationException(
        "Connection string 'ConnectionStrings:Dispatch' is not configured.");

builder.Services.AddDbContext<DispatchContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<IDispatchService, DispatchService>();

builder.Services.AddOptions<MessagingOptions>()
    .Bind(builder.Configuration.GetSection(MessagingOptions.SectionName));

var messaging = builder.Configuration
    .GetSection(MessagingOptions.SectionName)
    .Get<MessagingOptions>() ?? new MessagingOptions();

var healthChecks = builder.Services.AddHealthChecks();

if (messaging.Enabled)
{
    // One connection for the process; channels are cheap, connections are not.
    builder.Services.AddSingleton<IRabbitMqConnection, RabbitMqConnection>();
    builder.Services.AddSingleton<IIncidentPublisher, RabbitMqIncidentPublisher>();

    // Scoped: both take the request's (or the message's) DbContext, so the
    // idempotency mark and the work it guards share one transaction.
    builder.Services.AddScoped<IProcessedMessageStore, EfProcessedMessageStore>();
    builder.Services.AddScoped<NotificationHandler>();

    // Registered before the consumer so the queue exists before anything
    // consumes from it. Singleton as well as hosted service because the consumer
    // re-declares through it after a reconnect.
    builder.Services.AddSingleton<TopologyInitializer>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TopologyInitializer>());
    builder.Services.AddHostedService<NotificationConsumer>();

    healthChecks.AddCheck<RabbitMqHealthCheck>(
        "rabbitmq", failureStatus: HealthStatus.Unhealthy, tags: new[] { "ready" });
}
else
{
    builder.Services.AddSingleton<IIncidentPublisher, NullIncidentPublisher>();
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

// Liveness. Answers only "is this process still running", so it must not depend
// on anything the process cannot fix by restarting. A broker outage that failed
// this check would have the orchestrator kill every healthy instance.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });

// Readiness. Includes the broker, because an instance that cannot publish should
// not be taking traffic even though the process itself is fine.
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.MapDispatchEndpoints();

app.MapGet("/", () => Results.Redirect("/swagger"))
    .ExcludeFromDescription();

app.Run();
