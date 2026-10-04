namespace Odysseus.Application;

/// <summary>
/// Raised when the installer console overran the time it is allowed and was stopped.
/// </summary>
/// <remarks>
/// It has its own type rather than borrowing <see cref="TimeoutException"/> because the two need
/// opposite things said about them. A broker call that timed out wrote nothing, so asking again is
/// safe; an install that was stopped part way may well have written something, so the honest answer
/// names the capture of its output and says that the product directory has to be looked at before
/// anything is tried again.
///
/// The console cannot be asked to stop - it is handed no cancellation of any kind - so the only way to
/// end one is to kill it and its children. That is what happened by the time this is raised.
/// </remarks>
public sealed class ProductInstallerTimedOutException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="message">What was stopped, after how long, and where its output was captured.</param>
	public ProductInstallerTimedOutException(string message)
		: base(message)
	{
	}
}
