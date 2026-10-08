namespace StockSharp.Odysseus.Application.Tests;

using StockSharp.Odysseus.Platform;

/// <summary>
/// Cutting a run's result apart to see where it came from.
/// </summary>
/// <remarks>
/// A run reports one number for a whole stretch of history, and one number cannot say whether an edge
/// held throughout or whether a single fortnight paid for everything around it. The second is the
/// common case and the one a total hides, so the result is cut by calendar month, by part of the
/// session, by how long positions were held and by direction — and the run is asked what it is worth
/// without its best month, which is the question a total can never answer.
/// </remarks>
[TestClass]
public class RunBreakdownCalculatorTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	/// <summary>Nothing is lost in the cutting: the months add back up to the whole.</summary>
	[TestMethod]
	public void TheMonthsAddUpToTheWholeRun()
	{
		var trades = Spread();
		var breakdown = RunBreakdownCalculator.Measure(trades, new StockSharpMarketProfiler().ActiveSession(Bars()));

		AreEqual(trades.Sum(t => t.Net), breakdown.ByMonth.Sum(s => s.Net), "the months do not add up to the run.");
		AreEqual(trades.Count, breakdown.ByMonth.Sum(s => s.Trades), "trades went missing between the months.");
	}

	/// <summary>Every cut is of the same run, so each one adds up to the same total.</summary>
	[TestMethod]
	public void EveryCutIsOfTheSameRun()
	{
		var trades = Spread();
		var breakdown = RunBreakdownCalculator.Measure(trades, new StockSharpMarketProfiler().ActiveSession(Bars()));
		var total = trades.Sum(t => t.Net);

		AreEqual(total, breakdown.ByPartOfSession.Sum(s => s.Net), "the parts of the session do not add up.");
		AreEqual(total, breakdown.ByHoldingTime.Sum(s => s.Net), "the holding times do not add up.");
		AreEqual(total, breakdown.ByDirection.Sum(s => s.Net), "the directions do not add up.");
	}

	/// <summary>
	/// A run that made all its money in one month is asked what it is worth without that month, and
	/// answers with the loss underneath.
	/// </summary>
	[TestMethod]
	public void ARunThatRestsOnOneMonthSaysSo()
	{
		var trades = new List<ExecutedTrade>();

		// March pays for everything; April and May give a little of it back.
		for (var i = 0; i < 10; i++)
			trades.Add(Trade($"mar{i}", _open.AddDays(i), profit: 100m));

		for (var i = 0; i < 10; i++)
			trades.Add(Trade($"apr{i}", _open.AddMonths(1).AddDays(i), profit: -20m));

		for (var i = 0; i < 10; i++)
			trades.Add(Trade($"may{i}", _open.AddMonths(2).AddDays(i), profit: -20m));

		var breakdown = RunBreakdownCalculator.Measure(trades, new StockSharpMarketProfiler().ActiveSession(Bars()));

		AreEqual(600m, trades.Sum(t => t.Net), "the case itself is wrong.");
		AreEqual(-400m, breakdown.NetWithoutBestMonth, "removing the best month gave the wrong result.");
		AreEqual(3, breakdown.MonthsTraded);
		AreEqual(1, breakdown.MonthsInProfit);
	}

	/// <summary>A trade is counted in the part of the session it was opened in.</summary>
	[TestMethod]
	public void TradesAreCutByPartOfTheSession()
	{
		var trades = new List<ExecutedTrade>
		{
			Trade("early", _open.AddMinutes(5), profit: 10m),
			Trade("middle", _open.AddHours(3), profit: 20m),
		};

		var breakdown = RunBreakdownCalculator.Measure(trades, new StockSharpMarketProfiler().ActiveSession(Bars()));

		var early = breakdown.ByPartOfSession.Single(s => s.Name == "first 30 minutes");
		var middle = breakdown.ByPartOfSession.Single(s => s.Name == "middle of the session");

		AreEqual(1, early.Trades);
		AreEqual(10m, early.Net);
		AreEqual(1, middle.Trades);
		AreEqual(20m, middle.Net);
	}

	/// <summary>
	/// Holding times are cut where this run's own trades fall rather than at round numbers, so the same
	/// cut means something to a strategy that holds minutes and to one that holds weeks.
	/// </summary>
	[TestMethod]
	public void HoldingTimesAreCutWhereTheRunsOwnTradesFall()
	{
		var trades = new List<ExecutedTrade>();

		for (var i = 1; i <= 20; i++)
			trades.Add(Trade($"t{i}", _open.AddDays(i), profit: 10m, heldMinutes: i * 5));

		var breakdown = RunBreakdownCalculator.Measure(trades, new StockSharpMarketProfiler().ActiveSession(Bars()));

		AreEqual(4, breakdown.ByHoldingTime.Count, "holding time should be cut into quarters.");
		AreEqual(20, breakdown.ByHoldingTime.Sum(s => s.Trades), "trades went missing between the quarters.");

		// Cut at durations rather than at counts, so a quarter holds about a quarter rather than exactly
		// one: the boundary belongs to the part below it, and every trade sharing that duration with it.
		foreach (var quarter in breakdown.ByHoldingTime)
			IsTrue(quarter.Trades is >= 3 and <= 7, $"'{quarter.Name}' holds {quarter.Trades} of the twenty trades.");
	}

	/// <summary>
	/// A strategy that always holds for the same time has nothing to cut by holding time, and says so in
	/// one row rather than four identical ones.
	/// </summary>
	/// <remarks>
	/// A fixed exit is the common case — five bars, one session, a stop and nothing else — and cutting an
	/// unvarying quantity into quarters splits ties by whatever order they happened to be in. The four
	/// rows then carry four different results and read as a finding about holding time, when the strategy
	/// held every position for exactly as long as every other.
	/// </remarks>
	[TestMethod]
	public void AFixedHoldingTimeIsOneRowRatherThanFourIdenticalOnes()
	{
		var trades = new List<ExecutedTrade>();

		for (var i = 0; i < 20; i++)
			trades.Add(Trade($"t{i}", _open.AddDays(i), profit: i % 2 == 0 ? 40m : -20m, heldMinutes: 25));

		var breakdown = RunBreakdownCalculator.Measure(trades, new StockSharpMarketProfiler().ActiveSession(Bars()));

		AreEqual(1, breakdown.ByHoldingTime.Count,
			$"every trade was held the same time, yet it was cut into {breakdown.ByHoldingTime.Count} parts: " +
			$"{string.Join(", ", breakdown.ByHoldingTime.Select(s => s.Name))}.");

		AreEqual(20, breakdown.ByHoldingTime[0].Trades);
		IsTrue(breakdown.ByHoldingTime[0].Name.Contains("25", StringComparison.Ordinal),
			$"the row does not say how long: {breakdown.ByHoldingTime[0].Name}");
	}

	/// <summary>Trades held for the same time are never split across two parts.</summary>
	[TestMethod]
	public void TradesHeldTheSameTimeStayTogether()
	{
		var trades = new List<ExecutedTrade>();

		// Two populations, thirty of each, so a cut into equal counts would have to break one of them.
		for (var i = 0; i < 30; i++)
			trades.Add(Trade($"short{i}", _open.AddDays(i), profit: 10m, heldMinutes: 25));

		for (var i = 0; i < 30; i++)
			trades.Add(Trade($"long{i}", _open.AddDays(i), profit: -5m, heldMinutes: 240));

		var breakdown = RunBreakdownCalculator.Measure(trades, new StockSharpMarketProfiler().ActiveSession(Bars()));

		foreach (var part in breakdown.ByHoldingTime)
		{
			IsTrue(part.Trades == 30,
				$"'{part.Name}' holds {part.Trades} trades, so one of the two holding times was split.");
		}
	}

	/// <summary>A direction that was never traded is not reported as a row of zeroes.</summary>
	[TestMethod]
	public void ADirectionThatWasNeverTradedIsNotReported()
	{
		var breakdown = RunBreakdownCalculator.Measure(Spread(), new StockSharpMarketProfiler().ActiveSession(Bars()));

		IsTrue(breakdown.ByDirection.All(s => s.Trades > 0), "a direction with no trades was reported.");
		IsTrue(breakdown.ByDirection.Any(s => s.Name.Contains("long", StringComparison.Ordinal)), "the long trades were not reported.");
	}

	/// <summary>A run with no trades says nothing rather than dividing by nothing.</summary>
	[TestMethod]
	public void ARunWithNoTradesIsEmptyRatherThanWrong()
	{
		var breakdown = RunBreakdownCalculator.Measure([], new StockSharpMarketProfiler().ActiveSession(Bars()));

		AreEqual(0, breakdown.ByMonth.Count);
		AreEqual(0, breakdown.ByHoldingTime.Count);
		AreEqual(0m, breakdown.NetWithoutBestMonth);
		AreEqual(0, breakdown.MonthsTraded);
	}

	/// <summary>Twenty trades over three months, both directions, spread across the session.</summary>
	private static IReadOnlyList<ExecutedTrade> Spread()
	{
		var trades = new List<ExecutedTrade>();

		for (var i = 0; i < 20; i++)
		{
			trades.Add(Trade(
				$"t{i}",
				_open.AddDays(i * 4).AddMinutes(i % 4 * 45),
				profit: i % 3 == 0 ? -15m : 25m,
				heldMinutes: 10 + i * 3,
				direction: i % 5 == 0 ? TradeDirections.Short : TradeDirections.Long));
		}

		return trades;
	}

	private static ExecutedTrade Trade(
		string id,
		DateTime entryTime,
		decimal profit,
		int heldMinutes = 30,
		TradeDirections direction = TradeDirections.Long)
	{
		const decimal entry = 100m;
		const decimal volume = 10m;

		var move = profit / volume;
		var exit = direction == TradeDirections.Long ? entry + move : entry - move;

		return new(id, "NVDA", direction, entryTime, entry, entryTime.AddMinutes(heldMinutes), exit, volume, 0m, 0m);
	}

	/// <summary>Bars covering an American session, which is what the parts of the session are read from.</summary>
	private static IReadOnlyList<Candle> Bars()
	{
		var bars = new List<Candle>();

		for (var day = 0; day < 90; day++)
		{
			for (var i = 0; i < 78; i++)
			{
				var time = _open.AddDays(day).AddMinutes(5 * i);

				bars.Add(new(time, 100m, 100.5m, 99.5m, 100m, 1_000m));
			}
		}

		return bars;
	}
}
