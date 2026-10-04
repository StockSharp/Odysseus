namespace Odysseus.Domain.Tests;

using System.Linq;

/// <summary>
/// The candidate lifecycle. The order of stages is what makes the numbers mean anything: a candidate
/// that reached <see cref="CandidateStatuses.Completed"/> without passing through validation would
/// have been called finished on no evidence.
/// </summary>
[TestClass]
public class CandidateLifecycleTests : OdysseusTestBase
{
	/// <summary>The happy path must be walkable end to end.</summary>
	[TestMethod]
	public void ResearchPathIsWalkable()
	{
		var status = CandidateStatuses.Draft;

		CandidateStatuses[] path =
		[
			CandidateStatuses.SourceGenerated,
			CandidateStatuses.AnalysisPassed,
			CandidateStatuses.Compiled,
			CandidateStatuses.Backtested,
			CandidateStatuses.Optimized,
			CandidateStatuses.Validated,
			CandidateStatuses.StressTested,
			CandidateStatuses.FinalChecked,
			CandidateStatuses.Completed,
			CandidateStatuses.PaperRunning,
			CandidateStatuses.Stopped,
		];

		foreach (var next in path)
		{
			IsTrue(CandidateLifecycle.CanTransition(status, next),
				$"{status} -> {next} must be allowed.");

			status = next;
		}
	}

	/// <summary>
	/// Skipping a stage is the failure this type exists to prevent: calling a candidate finished is only
	/// worth as much as the evidence collected before it.
	/// </summary>
	[TestMethod]
	public void StagesCannotBeSkipped()
	{
		IsFalse(CandidateLifecycle.CanTransition(CandidateStatuses.Compiled, CandidateStatuses.Completed),
			"a candidate cannot be called finished straight after compiling: nothing has been measured yet.");

		IsFalse(CandidateLifecycle.CanTransition(CandidateStatuses.Draft, CandidateStatuses.Backtested),
			"a candidate cannot be backtested before its source exists.");

		IsFalse(CandidateLifecycle.CanTransition(CandidateStatuses.Backtested, CandidateStatuses.FinalChecked),
			"the closed slice cannot be reached before validation and stress.");
	}

	/// <summary>
	/// Paper trading starts from the closed-data measurement, whether or not the researcher has already
	/// called the candidate finished.
	/// </summary>
	[TestMethod]
	public void OnlyMeasuredCandidatesReachPaper()
	{
		IsTrue(CandidateLifecycle.CanTransition(CandidateStatuses.FinalChecked, CandidateStatuses.Completed));
		IsTrue(CandidateLifecycle.CanTransition(CandidateStatuses.FinalChecked, CandidateStatuses.PaperRunning));
		IsTrue(CandidateLifecycle.CanTransition(CandidateStatuses.Completed, CandidateStatuses.PaperRunning));

		IsFalse(CandidateLifecycle.CanTransition(CandidateStatuses.Validated, CandidateStatuses.PaperRunning),
			"a candidate the closed data has never seen must have no route to paper trading.");
	}

	/// <summary>
	/// A technical failure is not a result. The lifecycle keeps the two apart so that a crashed worker
	/// never reads as a losing strategy.
	/// </summary>
	[TestMethod]
	public void TechnicalFailureIsReachableFromAnyWorkingStage()
	{
		CandidateStatuses[] working =
		[
			CandidateStatuses.SourceGenerated,
			CandidateStatuses.Compiled,
			CandidateStatuses.Backtested,
			CandidateStatuses.Optimized,
		];

		foreach (var status in working)
		{
			IsTrue(CandidateLifecycle.CanTransition(status, CandidateStatuses.Failed),
				$"{status} must be able to end in a technical failure.");
		}

		IsFalse(CandidateLifecycle.CanTransition(CandidateStatuses.Completed, CandidateStatuses.Failed),
			"a candidate already called finished cannot be turned into a technical failure afterwards.");
	}

	/// <summary>A terminal status has no outgoing transition at all.</summary>
	[TestMethod]
	public void TerminalStatusesAreTerminal()
	{
		CandidateStatuses[] terminal =
		[
			CandidateStatuses.Failed,
			CandidateStatuses.Cancelled,
			CandidateStatuses.Stopped,
		];

		foreach (var status in terminal)
		{
			IsTrue(CandidateLifecycle.IsTerminal(status), $"{status} must be terminal.");

			foreach (var next in Enum.GetValues<CandidateStatuses>())
			{
				IsFalse(CandidateLifecycle.CanTransition(status, next),
					$"{status} is terminal but allows a transition to {next}.");
			}
		}
	}

	/// <summary>Research can be cancelled while it is still in flight, and never after it is concluded.</summary>
	[TestMethod]
	public void CancellationIsAllowedWhileWorkIsInFlight()
	{
		IsTrue(CandidateLifecycle.CanTransition(CandidateStatuses.Optimized, CandidateStatuses.Cancelled));
		IsTrue(CandidateLifecycle.CanTransition(CandidateStatuses.Draft, CandidateStatuses.Cancelled));

		IsFalse(CandidateLifecycle.CanTransition(CandidateStatuses.Completed, CandidateStatuses.Cancelled),
			"the candidate has already been called finished; cancelling it would rewrite history.");
	}

	/// <summary>Every status must be reachable, otherwise the enum and the machine have drifted apart.</summary>
	[TestMethod]
	public void EveryStatusIsReachableFromDraft()
	{
		var reached = CandidateLifecycle.ReachableFrom(CandidateStatuses.Draft);

		foreach (var status in Enum.GetValues<CandidateStatuses>())
		{
			if (status == CandidateStatuses.Draft)
				continue;

			IsTrue(reached.Contains(status),
				$"{status} is declared but cannot be reached from Draft; the enum and the machine disagree.");
		}
	}

	/// <summary>Transitioning through the guarded method must reject an illegal move rather than allow it.</summary>
	[TestMethod]
	public void GuardedTransitionThrowsOnIllegalMove()
	{
		var error = Throws<InvalidOperationException>(
			() => CandidateLifecycle.EnsureTransition(CandidateStatuses.Compiled, CandidateStatuses.Completed));

		IsTrue(error.Message.Contains("Compiled", StringComparison.Ordinal) &&
			error.Message.Contains("Completed", StringComparison.Ordinal),
			"the error must name both ends of the rejected transition.");
	}
}
