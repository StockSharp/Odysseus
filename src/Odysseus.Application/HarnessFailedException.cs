namespace StockSharp.Odysseus.Application;

/// <summary>
/// Which part of the machinery around a candidate failed.
/// </summary>
/// <remarks>
/// This is not <see cref="IsolationFailures"/> read again. That enum says how the worker ended; this one
/// says whose failure it was. A worker stopped on its deadline or its memory limit is the candidate's
/// answer - the machinery did exactly what it exists for, and what would not stop was somebody else's
/// arithmetic - while a worker that died mid-sentence, or one speaking a protocol this server does not,
/// says nothing about the strategy at all.
/// </remarks>
public enum HarnessFailures
{
	/// <summary>The process that runs candidates died, or answered something this server cannot read.</summary>
	Worker,

	/// <summary>What the run produced could not be written down.</summary>
	Storage,
}

/// <summary>
/// Raised when the machinery around a candidate failed rather than the candidate itself.
/// </summary>
/// <remarks>
/// A candidate that cannot be run has been answered: the attempt is recorded, it costs the allowance,
/// and proposing the same one again is refused by what is now on record. None of that reasoning survives
/// being applied to a broken machine. The strategy was never asked the question, so there is nothing to
/// have learned, and charging for it spends a finite allowance on a flaky afternoon and leaves a record
/// that cannot tell a defect in a hypothesis from a defect in a server.
///
/// So a failure of this kind is given back rather than charged, and it reaches the caller as something
/// to ask again rather than as a result. What stops that from being an unlimited free run is
/// <see cref="BacktestService.FreeInterruptions"/>.
/// </remarks>
public sealed class HarnessFailedException : Exception
{
	/// <summary>
	/// Creates the exception.
	/// </summary>
	/// <param name="kind">Which part of the machinery failed.</param>
	/// <param name="message">What broke, in the caller's terms and safe to publish.</param>
	/// <param name="inner">The failure underneath, which stays in the log.</param>
	public HarnessFailedException(HarnessFailures kind, string message, Exception inner)
		: base(message, inner)
	{
		Kind = kind;
	}

	/// <summary>Which part of the machinery failed.</summary>
	public HarnessFailures Kind { get; }
}
