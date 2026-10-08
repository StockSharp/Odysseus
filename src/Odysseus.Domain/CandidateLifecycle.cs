namespace StockSharp.Odysseus.Domain;

using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The transitions a candidate may make.
/// </summary>
/// <remarks>
/// The order is the point. Calling a candidate finished is a statement about evidence, so it cannot get
/// there without having passed the stages that produce the evidence, and it cannot quietly go back to
/// being unfinished afterwards.
/// </remarks>
public static class CandidateLifecycle
{
	private static readonly IReadOnlyDictionary<CandidateStatuses, CandidateStatuses[]> _transitions =
		new Dictionary<CandidateStatuses, CandidateStatuses[]>
		{
			[CandidateStatuses.Draft] = [CandidateStatuses.SourceGenerated],
			[CandidateStatuses.SourceGenerated] = [CandidateStatuses.AnalysisPassed],
			[CandidateStatuses.AnalysisPassed] = [CandidateStatuses.Compiled],
			[CandidateStatuses.Compiled] = [CandidateStatuses.Backtested],
			[CandidateStatuses.Backtested] = [CandidateStatuses.Optimized, CandidateStatuses.Validated],
			[CandidateStatuses.Optimized] = [CandidateStatuses.Validated],
			[CandidateStatuses.Validated] = [CandidateStatuses.StressTested],
			[CandidateStatuses.StressTested] = [CandidateStatuses.FinalChecked],
			[CandidateStatuses.FinalChecked] = [CandidateStatuses.Completed, CandidateStatuses.PaperRunning],
			[CandidateStatuses.Completed] = [CandidateStatuses.PaperRunning],
			[CandidateStatuses.PaperRunning] = [CandidateStatuses.Stopped, CandidateStatuses.Completed],
		};

	// Reached from any stage where work is still in flight. A candidate the researcher has called finished
	// is not one of them: failing or cancelling it afterwards would rewrite what was already concluded.
	private static readonly CandidateStatuses[] _workingStages =
	[
		CandidateStatuses.Draft,
		CandidateStatuses.SourceGenerated,
		CandidateStatuses.AnalysisPassed,
		CandidateStatuses.Compiled,
		CandidateStatuses.Backtested,
		CandidateStatuses.Optimized,
		CandidateStatuses.Validated,
		CandidateStatuses.StressTested,
		CandidateStatuses.FinalChecked,
	];

	/// <summary>
	/// Whether the candidate may move from one status to another.
	/// </summary>
	/// <param name="from">Current status.</param>
	/// <param name="to">Proposed status.</param>
	/// <returns><see langword="true"/> when the move is part of the declared lifecycle.</returns>
	public static bool CanTransition(CandidateStatuses from, CandidateStatuses to)
	{
		if (IsTerminal(from))
			return false;

		if (to is CandidateStatuses.Failed or CandidateStatuses.Cancelled)
			return _workingStages.Contains(from);

		return _transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
	}

	/// <summary>
	/// Moves from one status to another, refusing a move that is not part of the lifecycle.
	/// </summary>
	/// <param name="from">Current status.</param>
	/// <param name="to">Proposed status.</param>
	/// <returns>The new status.</returns>
	/// <exception cref="InvalidOperationException">The move is not part of the lifecycle.</exception>
	public static CandidateStatuses EnsureTransition(CandidateStatuses from, CandidateStatuses to)
	{
		if (!CanTransition(from, to))
			throw new InvalidOperationException($"A candidate cannot move from {from} to {to}.");

		return to;
	}

	/// <summary>
	/// Whether the status admits no further move.
	/// </summary>
	/// <param name="status">Status to test.</param>
	/// <returns><see langword="true"/> for a terminal status.</returns>
	public static bool IsTerminal(CandidateStatuses status)
		=> status is CandidateStatuses.Failed
			or CandidateStatuses.Cancelled
			or CandidateStatuses.Stopped;

	/// <summary>
	/// Every status reachable from the given one by any sequence of allowed moves.
	/// </summary>
	/// <param name="from">Status to start from.</param>
	/// <returns>The reachable set, excluding the starting status unless it is reachable again.</returns>
	public static IReadOnlySet<CandidateStatuses> ReachableFrom(CandidateStatuses from)
	{
		var reached = new HashSet<CandidateStatuses>();
		var pending = new Queue<CandidateStatuses>();

		pending.Enqueue(from);

		while (pending.Count > 0)
		{
			var current = pending.Dequeue();

			foreach (var next in Enum.GetValues<CandidateStatuses>())
			{
				if (!CanTransition(current, next) || !reached.Add(next))
					continue;

				pending.Enqueue(next);
			}
		}

		return reached;
	}
}
