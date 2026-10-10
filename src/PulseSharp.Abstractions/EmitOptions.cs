using System.Data.Common;

namespace PulseSharp;

/// <summary>
/// Options for <see cref="IPulse.Emit{TEvent}(StreamName, TEvent, EmitOptions, CancellationToken)"/>.
/// </summary>
public sealed class EmitOptions
{
    /// <summary>
    /// What the stream must look like for the emit to succeed. The default is <see cref="ExpectedVersion.Any"/>.
    /// </summary>
    public ExpectedVersion ExpectedVersion { get; init; }

    /// <summary>
    /// A database transaction to save the event in. The event is then saved together with your own data,
    /// and handlers receive it only after the transaction commits. Stores that do not use a database ignore it.
    /// </summary>
    public DbTransaction? Transaction { get; init; }
}
