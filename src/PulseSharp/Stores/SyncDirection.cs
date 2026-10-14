namespace PulseSharp.Stores;

/// <summary>
/// Which way events travel between this node and a remote node.
/// </summary>
public enum SyncDirection
{
    /// <summary>
    /// This node sends its own events to the remote node.
    /// </summary>
    Push = 0,

    /// <summary>
    /// This node receives the remote node's events.
    /// </summary>
    Pull = 1
}
