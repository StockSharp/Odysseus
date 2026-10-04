namespace Odysseus.Domain.Tests;

using System.Collections.Generic;

/// <summary>
/// The arithmetic a measurement does over its own runs.
/// </summary>
/// <remarks>
/// These are the numbers the product hands a researcher, and every one of them is worked out here and
/// nowhere else. An error in any of them arrives as a plausible-looking figure beside correct ones: a
/// spread taken over seven windows instead of eight is a fifteenth too wide, and nothing about the
/// answer says so. Each expected value below is derived from the inputs in the summary above it rather
/// than copied out of the property, because a value read off the implementation agrees with it by
/// construction.
/// </remarks>
[TestClass]
public class MeasurementTests : OdysseusTestBase
{
	private static readonly DateTime _measuredAt = new(2026, 4, 6, 17, 0, 0, DateTimeKind.Utc);

	/// <summary>
	/// Eight stretches returning 2, 4, 4, 4, 5, 5, 7 and 9 percent made 40 percent between them, and 40
	/// over eight is 5.
	/// </summary>
	[TestMethod]
	public void TheMeanIsWhatOneStretchMade()
	{
		AreEqual(5m, Windows([2m, 4m, 4m, 4m, 5m, 5m, 7m, 9m]).WalkForwardMean);
	}

	/// <summary>
	/// The same eight stretches sit -3, -1, -1, -1, 0, 0, 2 and 4 from their mean of 5. Squared those
	/// are 9, 1, 1, 1, 0, 0, 4 and 16, which add to 32. The windows are every stretch that was measured
	/// rather than a sample drawn from more of them, so the spread is over the eight there are: 32 / 8
	/// is 4, whose root is 2.
	/// </summary>
	/// <remarks>
	/// Dividing by seven — the correction for estimating a population from a sample of it — would give
	/// the root of 32 / 7, or 2.138. That is the whole reason this test states its own arithmetic: both
	/// numbers look like a spread, and only one of them answers the question the property asks.
	/// </remarks>
	[TestMethod]
	public void TheSpreadIsTakenOverEveryStretchThereIsRatherThanASampleOfThem()
	{
		AreEqual(2m, Windows([2m, 4m, 4m, 4m, 5m, 5m, 7m, 9m]).WalkForwardSpread,
			"the stretches are the whole of what was measured, so the spread divides by the eight of " +
			"them; dividing by seven would report 2.138 for the same runs.");
	}

	/// <summary>A single stretch is not apart from anything, so it made what it made and spreads over nothing.</summary>
	[TestMethod]
	public void OneStretchIsNotSpreadAgainstAnything()
	{
		var measurement = Windows([7m]);

		AreEqual(7m, measurement.WalkForwardMean);
		AreEqual(0m, measurement.WalkForwardSpread);
	}

	/// <summary>
	/// Six stretches returning 3, -2, 0, 7, -5 and 4 percent ended in profit three times. The stretch
	/// that ended where it started made nothing, and nothing is not profit.
	/// </summary>
	[TestMethod]
	public void OnlyTheStretchesThatMadeSomethingAreCountedUp()
	{
		AreEqual(3, Windows([3m, -2m, 0m, 7m, -5m, 4m]).PositiveWindows,
			"a stretch that returned zero was counted as one that ended in profit, which would make it " +
			"four of six.");
	}

	/// <summary>
	/// Held out, the candidate returned 12 percent and fell 4 percent at its worst, so it returned three
	/// times what the fall cost.
	/// </summary>
	[TestMethod]
	public void TheReturnIsWeighedAgainstTheFallItCost()
	{
		AreEqual(3m, Fall(returnPercent: 12m, maxDrawdownPercent: 4m).ReturnOverDrawdown);
	}

	/// <summary>
	/// A run that never fell has nothing to weigh its return against, so the ratio is undefined rather
	/// than the return itself: 12 for no fall and 1200 for a fall of 0.01 percent would read as though
	/// the run with the fall were a hundred times better.
	/// </summary>
	[TestMethod]
	public void AReturnThatNeverFellHasNoRatio()
	{
		IsNull(Fall(returnPercent: 12m, maxDrawdownPercent: 0m).ReturnOverDrawdown,
			"a run with no drawdown reported a ratio it does not have.");
	}

	/// <summary>
	/// Held out the candidate made 800 and under costs half again as high it made 600, so three quarters
	/// of the result survived them.
	/// </summary>
	[TestMethod]
	public void ResilienceIsTheShareOfTheResultThatSurvivedTheHigherCosts()
	{
		var resilience = Profits(heldOut: 800m, stressed: 600m).CostResilience;

		IsNotNull(resilience);
		AreEqual(0.75m, resilience.Value);
	}

	/// <summary>
	/// The same 800 turned into a loss of 200 under the higher costs. That is a share of -0.25: the
	/// costs took the result and a quarter of it again, which is a finding rather than an absence of one.
	/// </summary>
	[TestMethod]
	public void AResultTheCostsTurnedNegativeIsStillAShare()
	{
		var resilience = Profits(heldOut: 800m, stressed: -200m).CostResilience;

		IsNotNull(resilience, "a result that the costs took past zero was reported as no result at all.");
		AreEqual(-0.25m, resilience.Value);
	}

	/// <summary>
	/// A candidate that made nothing held out, or lost, has nothing for the higher costs to eat into.
	/// The share is absent rather than zero, because zero would say the costs took all of a result there
	/// never was.
	/// </summary>
	[TestMethod]
	public void ThereIsNoShareOfAResultThatWasNotMade()
	{
		IsNull(Profits(heldOut: 0m, stressed: 0m).CostResilience,
			"a candidate that made nothing was reported as having survived some share of it.");

		IsNull(Profits(heldOut: -250m, stressed: -400m).CostResilience,
			"a loss that grew under the higher costs was reported as a share of a profit.");
	}

	/// <summary>
	/// Four rules, three indicators and five searchable numbers are twelve ways the specification could
	/// have been tuned.
	/// </summary>
	[TestMethod]
	public void FreedomCountsEveryWayTheSpecificationCouldBeTuned()
	{
		AreEqual(12, Shaped(rules: 4, indicators: 3, parameters: 5).Freedom);
	}

	/// <summary>A measurement whose walk-forward stretches are the ones given and whose runs are blank.</summary>
	private static Measurement Windows(IReadOnlyList<decimal> returns)
		=> Measured(returns, Metrics(0m, 0m, 0m), Metrics(0m, 0m, 0m), new(0, 0, 0));

	/// <summary>A measurement whose held-out run made one amount and its stressed repeat another.</summary>
	private static Measurement Profits(decimal heldOut, decimal stressed)
		=> Measured([], Metrics(heldOut, 0m, 0m), Metrics(stressed, 0m, 0m), new(0, 0, 0));

	/// <summary>A measurement whose held-out run returned one percentage and fell by another.</summary>
	private static Measurement Fall(decimal returnPercent, decimal maxDrawdownPercent)
		=> Measured([], Metrics(0m, returnPercent, maxDrawdownPercent), Metrics(0m, 0m, 0m), new(0, 0, 0));

	/// <summary>A measurement of a specification of the given shape.</summary>
	private static Measurement Shaped(int rules, int indicators, int parameters)
		=> Measured([], Metrics(0m, 0m, 0m), Metrics(0m, 0m, 0m), new(rules, indicators, parameters));

	private static Measurement Measured(
		IReadOnlyList<decimal> windows,
		RunMetrics heldOut,
		RunMetrics heldOutStressed,
		SpecificationShape shape)
		=> new(
			CandidateId.New(),
			DataSlices.Validation,
			Metrics(0m, 0m, 0m),
			heldOut,
			heldOutStressed,
			windows,
			[],
			shape,
			_measuredAt);

	private static RunMetrics Metrics(decimal profit, decimal returnPercent, decimal maxDrawdownPercent)
		=> new(
			new(profit, returnPercent, null, 0m),
			new(profit, returnPercent, null, 0m),
			new(0m, 0m),
			new(maxDrawdownPercent, 0m, null),
			new(0, 0m, 0m),
			new(0m, 0m),
			new(0m, 0m, string.Empty, string.Empty),
			0);
}
