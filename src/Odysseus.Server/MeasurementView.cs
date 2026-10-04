namespace Odysseus.Server;

/// <summary>
/// Laying a measurement out for the model that asked for it.
/// </summary>
/// <remarks>
/// Numbers only. There is no verdict in here, no score and no threshold, because the server does not
/// hold an opinion about what a good strategy is - it runs one and says what came back. Reading those
/// numbers is the work of whoever is doing the research, and it is the part a threshold written into a
/// server would quietly take away from them.
/// </remarks>
public static class MeasurementView
{
	/// <summary>
	/// Describes one run.
	/// </summary>
	/// <param name="metrics">What the run measured.</param>
	/// <returns>The description.</returns>
	public static object Slice(RunMetrics metrics)
		=> new
		{
			netProfit = metrics.Net.Profit,
			returnPercent = metrics.Net.ReturnPercent,
			profitFactor = metrics.Net.ProfitFactor,
			averageTrade = metrics.Net.AverageTrade,
			grossProfit = metrics.Gross.Profit,
			costs = metrics.Costs.Total,
			maxDrawdownPercent = metrics.Risk.MaxDrawdownPercent,
			maxDrawdownAmount = metrics.Risk.MaxDrawdownAmount,
			recoveryFactor = metrics.Risk.RecoveryFactor,
			trades = metrics.Trades.Count,
			winRatePercent = metrics.Trades.WinRatePercent,
			averageHoldingMinutes = metrics.Trades.AverageHoldingMinutes,
			exposurePercent = metrics.Activity.ExposurePercent,
			turnover = metrics.Activity.Turnover,
			largestTradeProfitSharePercent = metrics.Concentration.LargestTradeProfitSharePercent,
			executionErrors = metrics.ExecutionErrorCount,
		};

	/// <summary>
	/// Describes a measurement.
	/// </summary>
	/// <param name="measurement">The measurement.</param>
	/// <returns>The description.</returns>
	public static object Of(Measurement measurement)
		=> new
		{
			candidateId = measurement.Candidate.Value,
			measuredAt = measurement.MeasuredAt,
			startingEquity = BacktestService.StartingEquity,
			heldOutSlice = measurement.HeldOutSlice.ToString(),
			development = Slice(measurement.Development),
			heldOut = Slice(measurement.HeldOut),
			heldOutStressed = Slice(measurement.HeldOutStressed),
			walkForward = new
			{
				returnPercentByWindow = measurement.WalkForwardReturns,
				mean = measurement.WalkForwardMean,
				spread = measurement.WalkForwardSpread,
				positiveWindows = measurement.PositiveWindows,
				windows = measurement.WalkForwardReturns.Count,
			},
			derived = new
			{
				returnOverDrawdown = measurement.ReturnOverDrawdown,
				costResilience = measurement.CostResilience,
				developmentToHeldOutReturnGap =
					measurement.Development.Net.ReturnPercent - measurement.HeldOut.Net.ReturnPercent,
			},
			// Named parts rather than a flag, because which one was silent changes what it means: nothing on
			// the part it was formed on is a hypothesis that describes nothing, nothing on the part held back
			// is a hypothesis that described only what it was formed on.
			whyNothingHappened = measurement.Silent.Count == 0
				? null
				: measurement.Silent.Select(s => new { slice = s.Slice, why = s.Why }).ToArray(),
			shape = new
			{
				rules = measurement.Shape.Rules,
				indicators = measurement.Shape.Indicators,
				parameters = measurement.Shape.Parameters,
				freedom = measurement.Freedom,
			},
		};

	/// <summary>
	/// Describes what measuring a candidate produced.
	/// </summary>
	/// <param name="result">The result.</param>
	/// <returns>The description.</returns>
	public static object Of(MeasurementResult result)
		=> new
		{
			measurement = Of(result.Measurement),
			runs = result.Runs.Select(r => r.Value).ToArray(),
			wasAlreadyMeasured = result.WasAlreadyMeasured,
			timesMeasured = result.TimesMeasured,
			howToRead =
				"These are the numbers, not a judgement of them. What counts as enough return, an " +
				"acceptable drawdown or too few trades depends on what is being researched, and this " +
				"server does not decide it. 'development' is the part the candidate was formed on and " +
				"'heldOut' the part it was not, named by 'heldOutSlice'; 'heldOutStressed' repeats that " +
				"run with costs half again as high. The walk-forward windows are consecutive stretches, so " +
				"they say whether one stretch carried the whole result. A part that traded nothing at all is " +
				"named under 'whyNothingHappened' with what the run made of it: no trades and no edge are " +
				"the same row of zeros and opposite findings.",
		};
}
