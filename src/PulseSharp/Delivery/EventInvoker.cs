namespace PulseSharp.Delivery;

internal delegate Task EventInvoker(
    IServiceProvider services,
    object @event,
    EventContext context,
    CancellationToken cancellationToken
);
