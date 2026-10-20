namespace PulseSharp.Testing;

/// <summary>
/// Anything that knows which events were emitted and which jobs were enqueued: <see cref="FakePulse"/> and
/// <see cref="PulseTestHost"/>. The checks in <see cref="PulseAssertions"/> work on it, and so do your own extension methods.
/// </summary>
public interface IRecordedPulse
{
    /// <summary>
    /// The events emitted, in order.
    /// </summary>
    IReadOnlyList<EmittedEvent> Events { get; }

    /// <summary>
    /// The jobs enqueued, in order.
    /// </summary>
    IReadOnlyList<EnqueuedJob> Jobs { get; }
}
