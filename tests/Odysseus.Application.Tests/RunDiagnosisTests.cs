namespace Odysseus.Application.Tests;

/// <summary>
/// Saying why a run measured nothing.
/// </summary>
/// <remarks>
/// Zero trades has two opposite meanings. Either the rules never triggered — a real answer about the
/// hypothesis — or they triggered and the orders were too large for the bars to fill, which is no answer
/// at all. Both arrive as the same empty row of numbers, so the run has to say which one it was.
/// </remarks>
[TestClass]
public class RunDiagnosisTests : OdysseusTestBase
{
	/// <summary>Orders that were placed and never filled are named as such, with both sizes.</summary>
	[TestMethod]
	public void OrdersTooLargeToFillAreExplained()
	{
		var said = RunDiagnosis.Explain(ordersPlaced: 41, trades: 0, volume: 1104m, bars: Bars(14m));

		IsNotNull(said, "a run that placed orders and filled none explained nothing.");

		IsTrue(said.Contains("1104", StringComparison.Ordinal), $"the order size is missing: {said}");
		IsTrue(said.Contains("14", StringComparison.Ordinal), $"what the bars traded is missing: {said}");
	}

	/// <summary>Rules that never triggered are a finding about the hypothesis, and say so instead.</summary>
	[TestMethod]
	public void RulesThatNeverTriggeredAreExplainedDifferently()
	{
		var said = RunDiagnosis.Explain(ordersPlaced: 0, trades: 0, volume: 10m, bars: Bars(1000m));

		IsNotNull(said, "a run that placed no orders explained nothing.");

		IsFalse(said.Contains("fill", StringComparison.OrdinalIgnoreCase),
			$"nothing was placed, so nothing failed to fill: {said}");
	}

	/// <summary>A run that traded needs no explanation, and inventing one would only be noise.</summary>
	[TestMethod]
	public void ARunThatTradedIsNotExplained()
	{
		IsNull(RunDiagnosis.Explain(ordersPlaced: 90, trades: 45, volume: 10m, bars: Bars(1000m)),
			"a run that traded was given an explanation it did not need.");
	}

	private static IReadOnlyList<Candle> Bars(decimal volume)
	{
		var bars = new List<Candle>();
		var time = new DateTime(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);

		for (var i = 0; i < 100; i++)
			bars.Add(new(time.AddMinutes(5 * i), 100m, 101m, 99m, 100m, volume));

		return bars;
	}
}
