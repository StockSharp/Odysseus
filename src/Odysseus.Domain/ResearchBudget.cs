namespace Odysseus.Domain;

using System;

/// <summary>
/// Persistable state of a <see cref="ResearchBudget"/>.
/// </summary>
/// <param name="MaxBacktests">Total backtests the project was granted.</param>
/// <param name="MaxCandidates">Total candidates the project was granted.</param>
/// <param name="MaxWallClock">Total machine time the project was granted.</param>
/// <param name="ClaimedBacktests">Backtests claimed so far.</param>
/// <param name="ClaimedCandidates">Candidates claimed so far.</param>
/// <param name="ClaimedWallClock">Machine time spent so far.</param>
public sealed record ResearchBudgetState(
	int MaxBacktests,
	int MaxCandidates,
	TimeSpan MaxWallClock,
	int ClaimedBacktests,
	int ClaimedCandidates,
	TimeSpan ClaimedWallClock);

/// <summary>
/// What one project's research was granted, and how much of it is left.
/// </summary>
/// <remarks>
/// The loop is driven by the user's agent, which has no reason to stop on its own, so the ceiling is
/// held by the server rather than asked of the caller. It is held where the figures are kept: a claim is
/// one statement in the store, which compares and adds in the same breath, so callers arriving together
/// cannot each find room for the last backtest. Allowance is taken before the expensive work starts -
/// counting afterwards would let a crash between the work and the count hand the budget back for free -
/// and given back only when the work it paid for never happened.
///
/// This type is the reading side of that: a snapshot of the stored figures, saying what a project was
/// granted and what it may still do. It spends nothing itself, and deliberately offers no way to. A
/// second place that could grant a claim would be a second answer to how much is left, and an object
/// that only ever holds a copy of the figures cannot decide that question for callers it cannot see.
/// </remarks>
public sealed class ResearchBudget
{
	private readonly int _claimedBacktests;
	private readonly int _claimedCandidates;
	private readonly TimeSpan _claimedWallClock;

	/// <summary>
	/// Creates a budget with nothing spent.
	/// </summary>
	/// <param name="maxBacktests">Backtests the project may run.</param>
	/// <param name="maxCandidates">Candidates the project may create.</param>
	/// <param name="maxWallClock">Machine time the project may consume.</param>
	/// <exception cref="ArgumentOutOfRangeException">A limit permits nothing.</exception>
	public ResearchBudget(int maxBacktests, int maxCandidates, TimeSpan maxWallClock)
		: this(maxBacktests, maxCandidates, maxWallClock, 0, 0, TimeSpan.Zero)
	{
	}

	private ResearchBudget(
		int maxBacktests,
		int maxCandidates,
		TimeSpan maxWallClock,
		int claimedBacktests,
		int claimedCandidates,
		TimeSpan claimedWallClock)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(maxBacktests, 1);
		ArgumentOutOfRangeException.ThrowIfLessThan(maxCandidates, 1);

		if (maxWallClock <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(maxWallClock), maxWallClock, "A budget must permit some time.");

		MaxBacktests = maxBacktests;
		MaxCandidates = maxCandidates;
		MaxWallClock = maxWallClock;

		_claimedBacktests = claimedBacktests;
		_claimedCandidates = claimedCandidates;
		_claimedWallClock = claimedWallClock;
	}

	/// <summary>
	/// The allowance a project starts with, whichever way in created it.
	/// </summary>
	/// <remarks>
	/// One ceiling rather than one per front door. The MCP server and the command line drive the same
	/// research over the same projects root, and a project started through either is worked on through
	/// both - so a limit that depended on which of the two wrote the row would be a limit anybody could
	/// raise by starting the project through the other one.
	///
	/// The count of backtests is what it is because a parameter search is one step that costs fifty of
	/// them. A handful of candidates, each searched and then put through the six runs a measurement is
	/// made from, is what this allows - and then it stops.
	/// </remarks>
	public static ResearchBudgetState Default { get; }
		= new ResearchBudget(maxBacktests: 400, maxCandidates: 40, maxWallClock: TimeSpan.FromMinutes(45)).ToState();

	/// <summary>Backtests the project was granted.</summary>
	public int MaxBacktests { get; }

	/// <summary>Candidates the project was granted.</summary>
	public int MaxCandidates { get; }

	/// <summary>Machine time the project was granted.</summary>
	public TimeSpan MaxWallClock { get; }

	/// <summary>Backtests still available.</summary>
	public int RemainingBacktests => MaxBacktests - _claimedBacktests;

	/// <summary>Candidates still available.</summary>
	public int RemainingCandidates => MaxCandidates - _claimedCandidates;

	/// <summary>Machine time still available.</summary>
	public TimeSpan RemainingWallClock
	{
		get
		{
			var left = MaxWallClock - _claimedWallClock;

			return left > TimeSpan.Zero ? left : TimeSpan.Zero;
		}
	}

	/// <summary>Whether any allowance is left at all.</summary>
	public bool IsExhausted
		=> RemainingBacktests == 0 || RemainingCandidates == 0 || RemainingWallClock == TimeSpan.Zero;

	/// <summary>
	/// Restores a budget from persisted state.
	/// </summary>
	/// <param name="state">State previously produced by <see cref="ToState"/>.</param>
	/// <returns>The restored budget.</returns>
	public static ResearchBudget Restore(ResearchBudgetState state)
	{
		ArgumentNullException.ThrowIfNull(state);

		return new(
			state.MaxBacktests,
			state.MaxCandidates,
			state.MaxWallClock,
			state.ClaimedBacktests,
			state.ClaimedCandidates,
			state.ClaimedWallClock);
	}

	/// <summary>
	/// Captures the state so that a restart resumes with the allowance already spent.
	/// </summary>
	/// <returns>The persistable state.</returns>
	public ResearchBudgetState ToState()
		=> new(MaxBacktests, MaxCandidates, MaxWallClock, _claimedBacktests, _claimedCandidates, _claimedWallClock);
}
