namespace PulseSharp.Testing;

/// <summary>
/// An event that was emitted, as a fake or the test host recorded it.
/// </summary>
/// <param name="Event">The event.</param>
/// <param name="Stream">The stream it was emitted to.</param>
/// <param name="Options">The options it was emitted with.</param>
public sealed record EmittedEvent(object Event, string Stream, EmitOptions Options);
