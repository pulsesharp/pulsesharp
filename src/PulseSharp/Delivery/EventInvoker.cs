namespace PulseSharp.Delivery;

internal delegate Task EventInvoker(
    IServiceProvider services,
    byte[] data,
    EventContext context,
    CancellationToken cancellationToken
);
