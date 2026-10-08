namespace StockSharp.Odysseus.Evaluation.Tests;

using System.Collections.Generic;

/// <summary>
/// Turning what a run did into the numbers a verdict is read from.
/// </summary>
/// <remarks>
/// Every number here is one a candidate lives or dies by, so each is measured against a case worked out
/// by hand rather than against whatever the code happens to produce. The cases are small on purpose: a
/// run of three trades can be checked on paper, and a metric that is wrong on three trades is wrong on
/// three thousand.
/// </remarks>
[TestClass]
public class RunMetricsCalculatorTests : OdysseusTestBase
{
	private static readonly DateTime _start = new(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);

	private static ExecutedTrade Trade(
		string id,
		decimal entry,
		decimal exit,
		decimal volume = 10m,
		string symbol = "NVDA",
		int openedAfterMinutes = 0,
		int heldMinutes = 30,
		decimal commission = 0m,
		decimal slippage = 0m,
		TradeDirections direction = TradeDirections.Long)
		=> new(
			id,
			symbol,
			direction,
			_start.AddMinutes(openedAfterMinutes),
			entry,
			_start.AddMinutes(openedAfterMinutes + heldMinutes),
			exit,
			volume,
			commission,
			slippage);

	private static IReadOnlyList<EquityPoint> Curve(params decimal[] values)
		=> [.. values.Select((v, i) => new EquityPoint(_start.AddMinutes(i * 5), v))];

	/// <summary>A run that never traded is measured as one that never traded, not as one that lost nothing.</summary>
	[TestMethod]
	public void ARunWithoutTradesReportsNothingRatherThanZeroes()
	{
		var metrics = RunMetricsCalculator.Measure([], Curve(100_000m, 100_000m), 100_000m, TimeSpan.FromDays(1));

		AreEqual(0, metrics.Trades.Count);
		IsNull(metrics.Net.ProfitFactor, "a ratio over no losing trades is undefined, not zero.");
		IsNull(metrics.Risk.RecoveryFactor, "a run that never declined has no recovery to report.");
		AreEqual(0m, metrics.Net.Profit);
	}

	/// <summary>The gross result is what the prices gave; the net result is what was left after costs.</summary>
	[TestMethod]
	public void CostsSeparateTheGrossResultFromTheNetOne()
	{
		var metrics = RunMetricsCalculator.Measure(
			[Trade("t1", 100m, 102m, commission: 4m, slippage: 1m)],
			Curve(100_000m, 100_015m),
			100_000m,
			TimeSpan.FromDays(1));

		AreEqual(20m, metrics.Gross.Profit, "two points on ten units is twenty before costs.");
		AreEqual(15m, metrics.Net.Profit, "the costs have not been taken off the net result.");
		AreEqual(4m, metrics.Costs.Commission);
		AreEqual(1m, metrics.Costs.Slippage);
		AreEqual(5m, metrics.Costs.Total);
	}

	/// <summary>
	/// The spread is a charge like the fees, not something already inside the price the run matched at,
	/// so it comes off the result once and shows up in the costs.
	/// </summary>
	[TestMethod]
	public void TheSpreadIsChargedOnceAndShown()
	{
		var metrics = RunMetricsCalculator.Measure(
			[Trade("t1", 100m, 102m, commission: 4m, slippage: 7m)],
			Curve(100_000m, 100_009m),
			100_000m,
			TimeSpan.FromDays(1));

		AreEqual(20m, metrics.Gross.Profit);
		AreEqual(9m, metrics.Net.Profit, "the spread was not taken off the result, or was taken off twice.");
		AreEqual(11m, metrics.Costs.Total);
		AreEqual(metrics.Gross.Profit - metrics.Net.Profit, metrics.Costs.Total,
			"the costs do not account for the whole gap between the gross and net results.");
	}

	/// <summary>A short position makes money when the price falls.</summary>
	[TestMethod]
	public void AShortPositionProfitsFromAFall()
	{
		var metrics = RunMetricsCalculator.Measure(
			[Trade("t1", 100m, 97m, direction: TradeDirections.Short)],
			Curve(100_000m, 100_030m),
			100_000m,
			TimeSpan.FromDays(1));

		AreEqual(30m, metrics.Net.Profit, "a short that fell three points on ten units made thirty.");
	}

	/// <summary>The profit factor weighs the winners against the losers.</summary>
	[TestMethod]
	public void TheProfitFactorWeighsWinnersAgainstLosers()
	{
		ExecutedTrade[] trades =
		[
			Trade("t1", 100m, 110m),
			Trade("t2", 100m, 105m, openedAfterMinutes: 60),
			Trade("t3", 100m, 95m, openedAfterMinutes: 120),
		];

		var metrics = RunMetricsCalculator.Measure(trades, Curve(100_000m, 100_100m), 100_000m, TimeSpan.FromDays(1));

		AreEqual(3m, metrics.Net.ProfitFactor, "a hundred and fifty of winners against fifty of losers is three.");
		AreEqual(3, metrics.Trades.Count);
		AreEqual(Math.Round(200m / 3, 4), Math.Round(metrics.Trades.WinRatePercent, 4));
		AreEqual(Math.Round(100m / 3, 4), Math.Round(metrics.Net.AverageTrade, 4));
	}

	/// <summary>A trade that ended exactly where it started is not a win.</summary>
	[TestMethod]
	public void ABreakEvenTradeIsNotCountedAsAWin()
	{
		var metrics = RunMetricsCalculator.Measure(
			[Trade("t1", 100m, 100m), Trade("t2", 100m, 110m, openedAfterMinutes: 60)],
			Curve(100_000m, 100_100m),
			100_000m,
			TimeSpan.FromDays(1));

		AreEqual(50m, metrics.Trades.WinRatePercent, "a flat trade was counted on the winning side.");
	}

	/// <summary>The drawdown is the deepest fall from a peak, not the fall from the start.</summary>
	[TestMethod]
	public void TheDrawdownIsMeasuredFromThePeak()
	{
		var metrics = RunMetricsCalculator.Measure(
			[Trade("t1", 100m, 102m)],
			Curve(100_000m, 110_000m, 88_000m, 120_000m, 114_000m),
			100_000m,
			TimeSpan.FromDays(1));

		AreEqual(22_000m, metrics.Risk.MaxDrawdownAmount, "the deepest fall was from 110000 to 88000.");
		AreEqual(20m, metrics.Risk.MaxDrawdownPercent, "22000 off a peak of 110000 is twenty percent.");
	}

	/// <summary>
	/// The deepest fall in money and the deepest fall in percent can be different falls, and each is
	/// reported for itself.
	/// </summary>
	/// <remarks>
	/// 100000 to 50000 loses half the account; 1000000 to 900000 later loses more money but only a tenth.
	/// </remarks>
	[TestMethod]
	public void TheDrawdownPercentIsTheDeepestFallInPercent()
	{
		var metrics = RunMetricsCalculator.Measure(
			[Trade("t1", 100m, 102m)],
			Curve(100_000m, 50_000m, 1_000_000m, 900_000m),
			100_000m,
			TimeSpan.FromDays(1));

		AreEqual(100_000m, metrics.Risk.MaxDrawdownAmount, "the deepest fall in money was from 1000000 to 900000.");
		AreEqual(50m, metrics.Risk.MaxDrawdownPercent, "the fall from 100000 to 50000 lost half the account.");
	}

	/// <summary>The recovery factor is the profit measured against what it cost to sit through.</summary>
	[TestMethod]
	public void TheRecoveryFactorComparesProfitWithTheWorstFall()
	{
		var metrics = RunMetricsCalculator.Measure(
			[Trade("t1", 100m, 1_100m)],
			Curve(100_000m, 90_000m, 110_000m),
			100_000m,
			TimeSpan.FromDays(1));

		AreEqual(10_000m, metrics.Net.Profit);
		AreEqual(1m, metrics.Risk.RecoveryFactor, "ten thousand of profit against ten thousand of drawdown is one.");
	}

	/// <summary>Turnover counts both sides, because both are traded and both are charged.</summary>
	[TestMethod]
	public void TurnoverCountsBothSidesOfEveryTrade()
	{
		var metrics = RunMetricsCalculator.Measure(
			[Trade("t1", 100m, 102m, volume: 10m)],
			Curve(100_000m, 100_020m),
			100_000m,
			TimeSpan.FromDays(1));

		AreEqual(2_020m, metrics.Activity.Turnover, "entering at 100 and leaving at 102 on ten units moves 2020.");
	}

	/// <summary>Exposure is the share of the slice spent holding something.</summary>
	[TestMethod]
	public void ExposureIsTheShareOfTheSliceSpentInTheMarket()
	{
		ExecutedTrade[] trades =
		[
			Trade("t1", 100m, 102m, heldMinutes: 30),
			Trade("t2", 100m, 102m, openedAfterMinutes: 60, heldMinutes: 30),
		];

		var metrics = RunMetricsCalculator.Measure(trades, Curve(100_000m, 100_040m), 100_000m, TimeSpan.FromMinutes(600));

		AreEqual(10m, metrics.Activity.ExposurePercent, "sixty minutes held out of six hundred is ten percent.");
		AreEqual(30m, metrics.Trades.AverageHoldingMinutes);
	}

	/// <summary>
	/// Concentration says whether the result is a strategy or a single lucky trade, which is the whole
	/// point of reporting it at all.
	/// </summary>
	[TestMethod]
	public void ConcentrationNamesTheTradeAndSymbolBehindTheResult()
	{
		ExecutedTrade[] trades =
		[
			Trade("t1", 100m, 190m, symbol: "NVDA"),
			Trade("t2", 100m, 105m, symbol: "AMD", openedAfterMinutes: 60),
			Trade("t3", 100m, 105m, symbol: "AMD", openedAfterMinutes: 120),
		];

		var metrics = RunMetricsCalculator.Measure(trades, Curve(100_000m, 101_000m), 100_000m, TimeSpan.FromDays(1));

		AreEqual(1_000m, metrics.Net.Profit);
		AreEqual(90m, metrics.Concentration.LargestTradeProfitSharePercent);
		AreEqual("t1", metrics.Concentration.LargestTradeId);
		AreEqual(90m, metrics.Concentration.LargestSymbolProfitSharePercent);
		AreEqual("NVDA", metrics.Concentration.LargestSymbol);
	}

	/// <summary>
	/// A losing run has no share to divide, and a share of a negative total would read as though one
	/// trade carried a result that does not exist.
	/// </summary>
	[TestMethod]
	public void ALosingRunHasNoConcentrationShare()
	{
		var metrics = RunMetricsCalculator.Measure(
			[Trade("t1", 100m, 90m), Trade("t2", 100m, 95m, openedAfterMinutes: 60)],
			Curve(100_000m, 99_850m),
			100_000m,
			TimeSpan.FromDays(1));

		IsTrue(metrics.Net.Profit < 0);
		AreEqual(0m, metrics.Concentration.LargestTradeProfitSharePercent,
			"a share of a loss was reported as though part of a profit came from one trade.");
	}

	/// <summary>The return is measured against the money the slice started with.</summary>
	[TestMethod]
	public void TheReturnIsMeasuredAgainstTheStartingEquity()
	{
		var metrics = RunMetricsCalculator.Measure(
			[Trade("t1", 100m, 350m)],
			Curve(50_000m, 52_500m),
			50_000m,
			TimeSpan.FromDays(1));

		AreEqual(2_500m, metrics.Net.Profit);
		AreEqual(5m, metrics.Net.ReturnPercent);
	}

	/// <summary>Measuring the same run twice gives the same numbers.</summary>
	[TestMethod]
	public void MeasurementIsRepeatable()
	{
		ExecutedTrade[] trades = [Trade("t1", 100m, 110m), Trade("t2", 100m, 95m, openedAfterMinutes: 60)];

		var curve = Curve(100_000m, 100_100m, 99_950m, 100_050m);

		AreEqual(
			RunMetricsCalculator.Measure(trades, curve, 100_000m, TimeSpan.FromDays(1)),
			RunMetricsCalculator.Measure(trades, curve, 100_000m, TimeSpan.FromDays(1)));
	}

	/// <summary>A slice with no tradable time cannot report an exposure share.</summary>
	[TestMethod]
	public void AnEmptySliceIsRefusedRatherThanDividedBy()
	{
		Throws<ArgumentOutOfRangeException>(
			() => RunMetricsCalculator.Measure([], Curve(100_000m), 100_000m, TimeSpan.Zero));
	}

	/// <summary>A run measured against no starting money is refused rather than divided by.</summary>
	[TestMethod]
	public void AnEmptyAccountIsRefusedRatherThanDividedBy()
	{
		Throws<ArgumentOutOfRangeException>(
			() => RunMetricsCalculator.Measure([], Curve(0m), 0m, TimeSpan.FromDays(1)));
	}
}
