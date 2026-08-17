using DispatchApi.Data;
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
