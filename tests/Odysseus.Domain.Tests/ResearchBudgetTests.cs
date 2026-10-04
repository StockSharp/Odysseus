namespace Odysseus.Domain.Tests;

using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Domain;
using Odysseus.TestKit;

/// <summary>
/// The research budget: what a project was granted, and what the figures say is left of it.
/// </summary>
/// <remarks>
/// The loop is driven by an autonomous agent that will not stop itself, so the limit has to be held by
/// the server, and it has to be taken before the expensive work starts rather than counted after it
/// finishes. Taking it happens in the store, inside the statement that changes the figure, and the tests
/// for it live there with it: a claim decided against a copy of the number is not a limit, however
/// carefully it is written. So this type reads and does not spend, and what is tested here is the
/// reading - which is what every answer about a project's remaining allowance is made of.
/// </remarks>
[TestClass]
public class ResearchBudgetTests : OdysseusTestBase
{
	private static ResearchBudget Small()
		=> new(maxBacktests: 3, maxCandidates: 2, maxWallClock: TimeSpan.FromMinutes(45));

	private static ResearchBudget Spent(int backtests, int candidates, TimeSpan wallClock)
		=> ResearchBudget.Restore(new(3, 2, TimeSpan.FromMinutes(45), backtests, candidates, wallClock));

	/// <summary>A budget nothing has been taken from holds all of what it was granted.</summary>
	[TestMethod]
	public void ANewBudgetHoldsEverythingItWasGranted()
	{
		var budget = Small();

		AreEqual(3, budget.RemainingBacktests);
		AreEqual(2, budget.RemainingCandidates);
		AreEqual(TimeSpan.FromMinutes(45), budget.RemainingWallClock);
		IsFalse(budget.IsExhausted);
	}

	/// <summary>What was claimed is gone from what is left, and gone from one allowance only.</summary>
	[TestMethod]
	public void WhatWasClaimedIsGoneFromWhatIsLeft()
	{
		var budget = Spent(backtests: 1, candidates: 0, wallClock: TimeSpan.FromMinutes(5));

		AreEqual(2, budget.RemainingBacktests);
		AreEqual(TimeSpan.FromMinutes(40), budget.RemainingWallClock);
		AreEqual(2, budget.RemainingCandidates, "a claimed backtest was charged to the candidates as well.");
	}

	/// <summary>
	/// Running out of any one of the three is running out: candidates left and no machine time to measure
	/// them in is a project that can do nothing further.
	/// </summary>
	[TestMethod]
	public void RunningOutOfAnyOneAllowanceExhaustsTheBudget()
	{
		IsTrue(Spent(3, 0, TimeSpan.Zero).IsExhausted, "a project that ran its every backtest says it has allowance.");
		IsTrue(Spent(0, 2, TimeSpan.Zero).IsExhausted, "a project that built its every candidate says it has allowance.");

		IsTrue(Spent(0, 0, TimeSpan.FromMinutes(45)).IsExhausted,
			"a project that spent its every minute says it has allowance.");

		IsFalse(Spent(2, 1, TimeSpan.FromMinutes(44)).IsExhausted, "a project with something left of each says it has none.");
	}

	/// <summary>
	/// Machine time spent past the allowance leaves none of it rather than less than none.
	/// </summary>
	/// <remarks>
	/// Time is the one allowance that is charged as work finishes rather than claimed before it starts,
	/// because how long a run takes is not known until it has taken it. So the last run can land after the
	/// allowance was already gone, and what a project has left is nothing - never a negative figure to be
	/// reported to the agent or subtracted from somewhere else.
	/// </remarks>
	[TestMethod]
	public void TimeSpentPastTheAllowanceLeavesNone()
	{
		var budget = Spent(0, 0, TimeSpan.FromMinutes(50));

		AreEqual(TimeSpan.Zero, budget.RemainingWallClock, "an overrun left the project owing time rather than out of it.");
		IsTrue(budget.IsExhausted);
	}

	/// <summary>
	/// Restoring after a restart must reproduce the figures, or a crash becomes a way to obtain more
	/// budget than the project was granted.
	/// </summary>
	[TestMethod]
	public void StateSurvivesRoundTrip()
	{
		var budget = Spent(backtests: 1, candidates: 1, wallClock: TimeSpan.FromMinutes(7));

		var restored = ResearchBudget.Restore(budget.ToState());

		AreEqual(budget.RemainingBacktests, restored.RemainingBacktests);
		AreEqual(budget.RemainingCandidates, restored.RemainingCandidates);
		AreEqual(budget.RemainingWallClock, restored.RemainingWallClock);
		AreEqual(budget.MaxBacktests, restored.MaxBacktests);
		AreEqual(budget.MaxCandidates, restored.MaxCandidates);
		AreEqual(budget.MaxWallClock, restored.MaxWallClock);
	}

	/// <summary>A budget that permits nothing is a configuration error, not a valid budget.</summary>
	[TestMethod]
	public void LimitsMustBePositive()
	{
		Throws<ArgumentOutOfRangeException>(
			() => new ResearchBudget(maxBacktests: 0, maxCandidates: 1, maxWallClock: TimeSpan.FromMinutes(1)));

		Throws<ArgumentOutOfRangeException>(
			() => new ResearchBudget(maxBacktests: 1, maxCandidates: 1, maxWallClock: TimeSpan.Zero));
	}

	/// <summary>
	/// A budget read back from a project is checked like any other, so a stored ceiling that permits
	/// nothing is refused where it is read rather than turning into a project that can never run anything.
	/// </summary>
	[TestMethod]
	public void RestoredLimitsMustBePositiveToo()
	{
		Throws<ArgumentOutOfRangeException>(
			() => ResearchBudget.Restore(new(0, 1, TimeSpan.FromMinutes(1), 0, 0, TimeSpan.Zero)));

		Throws<ArgumentOutOfRangeException>(
			() => ResearchBudget.Restore(new(1, 1, TimeSpan.Zero, 0, 0, TimeSpan.Zero)));
	}
}
