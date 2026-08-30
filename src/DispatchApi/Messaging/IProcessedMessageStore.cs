namespace DispatchApi.Messaging;

/// <summary>
/// Remembers which deliveries this consumer has already handled.
///
/// Stage rather than Save, deliberately. If the store committed on its own, a
/// crash between marking the message handled and saving the work it caused
/// would lose the work permanently: the redelivery would be skipped as a
/// duplicate. The caller owns the transaction so that the mark and the work land
/// together or not at all.
/// </summary>
public interface IProcessedMessageStore
{
    Task<bool> HasProcessedAsync(string consumer, string messageId, CancellationToken ct = default);

    void Stage(string consumer, string messageId);
}
