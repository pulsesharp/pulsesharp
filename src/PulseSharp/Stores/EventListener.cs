namespace PulseSharp.Stores;

/// <summary>
/// One handler listening to one kind of event. A store uses these to tell which events every listening handler has
/// already handled (see <see cref="IPulseStore.DeleteHandledEvents"/>).
/// </summary>
/// <param name="HandlerName">The handler's stable name.</param>
/// <param name="EventName">The stored name of an event type the handler listens to.</param>
public sealed record EventListener(string HandlerName, string EventName);
