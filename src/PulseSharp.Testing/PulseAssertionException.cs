namespace PulseSharp.Testing;

/// <summary>
/// Thrown when a PulseSharp test check fails, for example <c>ShouldHaveOne</c> or <c>WaitUntilIdle</c>.
/// Every test framework reports it as a failed test, with its message.
/// </summary>
public sealed class PulseAssertionException : Exception
{
    /// <summary>
    /// Creates the exception.
    /// </summary>
    /// <param name="message">What was expected and what happened.</param>
    public PulseAssertionException(string message)
        : base(message)
    {
    }
}
