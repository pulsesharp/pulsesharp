namespace PulseSharp.Jobs;

internal delegate Task JobInvoker(
    IServiceProvider services,
    object job,
    JobContext context,
    CancellationToken cancellationToken
);

internal sealed record JobRegistration(string JobName, object Source, JobInvoker Invoke);
