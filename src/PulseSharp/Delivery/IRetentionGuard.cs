namespace PulseSharp.Delivery;

/// <summary>
/// Something that knows which of this app's own events may not be deleted yet, because they were not sent to a server.
/// </summary>
internal interface IRetentionGuard
{
    /// <summary>The global position up to which this app's own events were sent, or null when nothing needs to be sent.</summary>
    Task<long?> OwnEventsSentUpTo(CancellationToken cancellationToken);
}
