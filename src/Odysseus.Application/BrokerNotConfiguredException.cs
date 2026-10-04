namespace Odysseus.Application;

/// <summary>
/// Raised when something that needs a broker account is asked for on a server that has none.
/// </summary>
/// <remarks>
/// Not a defect and not a bad request: the server is running exactly as configured, and most of what it
/// does needs no account at all. It is worth its own type so the answer can say which of the two the
/// caller is looking at, and what to do about it.
/// </remarks>
public sealed class BrokerNotConfiguredException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="what">What was being attempted, in a few words.</param>
	public BrokerNotConfiguredException(string what)
		: base($"{what} needs a broker account, and this server was started without one.")
	{
	}
}
