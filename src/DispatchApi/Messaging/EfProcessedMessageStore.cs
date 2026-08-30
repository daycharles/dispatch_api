using DispatchApi.Data;
using DispatchApi.Models;
using DispatchApi.Services;
using Microsoft.EntityFrameworkCore;

namespace DispatchApi.Messaging;

/// <summary>
/// Idempotency in the same database as the work, which is what lets the two be
/// committed atomically. A separate store (Redis, say) would be faster and would
/// reintroduce exactly the dual-write problem this is here to avoid.
/// </summary>
public sealed class EfProcessedMessageStore : IProcessedMessageStore
{
    private readonly DispatchContext _db;
    private readonly IClock _clock;

    public EfProcessedMessageStore(DispatchContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// A read, not a lock. It makes the common redelivery cheap; the composite
    /// primary key on (Consumer, MessageId) is what actually makes a duplicate
    /// impossible when two instances race.
    /// </summary>
    public Task<bool> HasProcessedAsync(string consumer, string messageId, CancellationToken ct = default) =>
        _db.ProcessedMessages.AnyAsync(m => m.Consumer == consumer && m.MessageId == messageId, ct);

    public void Stage(string consumer, string messageId) =>
        _db.ProcessedMessages.Add(new ProcessedMessage
        {
            Consumer = consumer,
            MessageId = messageId,
            ProcessedAtUtc = _clock.UtcNow
        });
}
