namespace PulseSharp.Stores;

/// <summary>
/// The answer to a handler lease request.
/// </summary>
/// <param name="Held">Whether the asking runner holds the lease now.</param>
/// <param name="Revision">The handler's current revision. A change means the cursors were changed by someone else.</param>
public readonly record struct HandlerLeaseStatus(bool Held, long Revision);
