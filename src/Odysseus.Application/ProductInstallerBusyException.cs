namespace Odysseus.Application;

using System;

/// <summary>
/// Raised when another installer holds the machine.
/// </summary>
/// <remarks>
/// The StockSharp installer is a machine-wide singleton: it holds one named pipe that accepts a single
/// server instance and one global mutex, and a second copy does not queue behind the first - it asks the
/// first to close and fails when it will not. So this is a conflict rather than a configuration
/// problem, and it is worth its own type because the answer to it is to wait or to close a window,
/// which is nothing like the answer to a console that is not installed.
/// </remarks>
public sealed class ProductInstallerBusyException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">What is holding the machine, and what to do about it.</param>
	public ProductInstallerBusyException(string message)
		: base(message)
	{
	}
}
