namespace StockSharp.Odysseus.Application;

/// <summary>
/// Raised when a connector was named but will not be loaded.
/// </summary>
/// <remarks>
/// A package outside the allow-list, an adapter type that is not in it, a setting the adapter does not
/// declare, or a build that wants a newer platform than this server carries. Every message names the
/// offending value, because the failure is the next prompt.
/// </remarks>
public sealed class ConnectorRefusedException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">What was refused, and why.</param>
	public ConnectorRefusedException(string message)
		: base(message)
	{
	}
}
