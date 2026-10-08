namespace StockSharp.Odysseus.Application;

/// <summary>
/// Raised when a connector cannot be proven to be pointed at a paper venue.
/// </summary>
/// <remarks>
/// This is the product's whole paper-only claim, in one refusal. A connector that has no way of being
/// told it is on paper is refused rather than used carefully, because there is no careful: the next
/// order it sends is a real one, and nothing downstream would look any different.
/// </remarks>
public sealed class ConnectorNotPaperException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">What could not be proven, and about which connector.</param>
	public ConnectorNotPaperException(string message)
		: base(message)
	{
	}
}
