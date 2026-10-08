namespace StockSharp.Odysseus.Application;

/// <summary>
/// How the isolated worker failed, as opposed to how a candidate failed.
/// </summary>
/// <remarks>
/// The distinction is the whole point of running a candidate somewhere else. A strategy that throws is
/// a result - it is recorded, it costs the allowance, and the agent learns something. A worker that was
/// killed, ran out of memory or answered with the wrong protocol is a defect or a limit, and telling the
/// agent it was a failed strategy would be a lie it cannot check.
///
/// These four are not one answer either, and where the line falls between them is stated once, in
/// <see cref="HarnessFailures"/>: a deadline or a memory limit is what the candidate cost, and a worker
/// that died or could not be spoken to is what this server cost.
/// </remarks>
public enum IsolationFailures
{
	/// <summary>The worker was still running when its deadline passed.</summary>
	Timeout,

	/// <summary>The worker went over the memory it is allowed.</summary>
	Memory,

	/// <summary>The worker exited without answering.</summary>
	Crashed,

	/// <summary>The worker did not greet, or greeted with an engine this server was not built against.</summary>
	Handshake,
}

/// <summary>
/// Raised when the isolated worker failed rather than the candidate inside it.
/// </summary>
public sealed class IsolationFailedException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="kind">Which of the four happened.</param>
	/// <param name="message">What happened, in the caller's terms.</param>
	public IsolationFailedException(IsolationFailures kind, string message)
		: base(message)
	{
		Kind = kind;
	}

	/// <summary>
	/// Creates the exception over the failure that produced it.
	/// </summary>
	/// <param name="kind">Which of the four happened.</param>
	/// <param name="message">What happened, in the caller's terms.</param>
	/// <param name="inner">The failure underneath, which stays in the log.</param>
	public IsolationFailedException(IsolationFailures kind, string message, Exception inner)
		: base(message, inner)
	{
		Kind = kind;
	}

	/// <summary>Which of the four happened.</summary>
	public IsolationFailures Kind { get; }
}
