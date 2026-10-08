namespace StockSharp.Odysseus.Application;

/// <summary>
/// Raised when a product cannot be installed because this machine is not set up to install one.
/// </summary>
/// <remarks>
/// Not a defect and not a bad request, and the same answer <see cref="BrokerNotConfiguredException"/>
/// gives for a missing broker: the server is running exactly as configured, everything that does not
/// touch a product still works, and the caller is told which of the three things is missing rather than
/// being handed a failure raised while a program that is not there was being started.
///
/// The three are: the installer console, which the operator supplies and this server never obtains;
/// a StockSharp account signed in on this machine, which is where the console reads its credentials
/// from; and the operator's list of product identifiers, which is empty unless somebody wrote one.
/// </remarks>
public sealed class ProductInstallerUnavailableException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">What is missing, where it was looked for, and what still works.</param>
	public ProductInstallerUnavailableException(string message)
		: base(message)
	{
	}
}
