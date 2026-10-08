namespace StockSharp.Odysseus.Application;

/// <summary>
/// Raised when a call is not available in the mode this server runs in.
/// </summary>
/// <remarks>
/// The mode is chosen when the process starts and is deliberately unreachable from any tool, so that
/// whatever comes to depend on it, an agent cannot talk its way into the wider of the two. This is what
/// that refusal looks like from the outside.
/// </remarks>
public sealed class ServerModeException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">What is not available here, and why.</param>
	public ServerModeException(string message)
		: base(message)
	{
	}
}
