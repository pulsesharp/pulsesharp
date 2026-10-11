namespace PulseSharp.Jobs;

internal delegate Task JobInvoker(
    IServiceProvider services,
    byte[] data,
    JobContext context,
    CancellationToken cancellationToken
);

internal sealed record JobRegistration(string JobName, object Source, JobInvoker Invoke);
