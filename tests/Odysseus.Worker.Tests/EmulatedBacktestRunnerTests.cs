namespace StockSharp.Odysseus.Worker.Tests;

using StockSharp.Odysseus.Evaluation;

/// <summary>
/// Running a generated strategy through the market emulator.
/// </summary>
/// <remarks>
/// This is the only place where the whole chain is exercised at once: a specification becomes C#,
/// the C# becomes an assembly, the assembly becomes a strategy the engine drives over bars, and what
/// comes back becomes trades and an account value. Everything upstream can be right while this is
/// wrong, and the failure would look like a strategy that simply never traded.
///
/// The bars are made rather than downloaded, and made so the answer is known: a price that walks up
/// and back down on a fixed period crosses its own average a countable number of times.
/// </remarks>
[TestClass]
public class EmulatedBacktestRunnerTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);

	/// <summary>
	/// A strategy whose rules are met really trades, and what it did comes back as trades rather than
	/// as a number nobody can look into.
	/// </summary>
	[TestMethod]
	public async Task AStrategyWhoseRulesAreMetTrades()
	{
		var outcome = await new EmulatedBacktestRunner().RunAsync(
			await RequestAsync(Oscillating(), Waves(600), ExecutionCosts.Default),
			CancellationToken);

		IsTrue(outcome.BarsProcessed > 500, $"the strategy saw only {outcome.BarsProcessed} of 600 bars.");
		IsTrue(outcome.Trades.Count > 0, "the rules were met repeatedly and the strategy never traded.");
		AreEqual(0, outcome.ExecutionErrorCount, "orders were refused during the run.");

		foreach (var trade in outcome.Trades)
		{
			AreEqual(TradeDirections.Long, trade.Direction);
			AreEqual(10m, trade.Volume);
			IsTrue(trade.ExitTime > trade.EntryTime, "a trade closed before it opened.");
		}
	}

	/// <summary>
	/// Nothing fills inside the bar the decision was made on. The decision is taken when a bar closes and
	/// the order is filled at the price the market stood at then; a run that filled earlier would be
	/// trading on a close it could not yet have known, which is the one flaw that makes every other
	/// number here worthless.
	/// </summary>
	/// <remarks>
	/// Which bar the decision was taken on is worked out from the entry rule over the bars rather than
	/// from the moment the fill came back, so a fill moved by one bar moves which bar has to satisfy the
	/// rule. The bars here open away from the close before them, so the price says which of the two the
	/// fill was taken at.
	/// </remarks>
	[TestMethod]
	public async Task NothingFillsOnTheBarTheDecisionWasMadeOn()
	{
		var bars = Waves(600);
		var signalled = EntriesSignalledBy(bars);

		var outcome = await new EmulatedBacktestRunner().RunAsync(
			await RequestAsync(Oscillating(), bars, ExecutionCosts.Default),
			CancellationToken);

		IsTrue(outcome.Trades.Count > 0);

		var separable = 0;

		foreach (var trade in outcome.Trades)
		{
			var filled = IndexOfBarOpeningAt(bars, trade.EntryTime);

			IsTrue(filled > 0,
				$"trade {trade.Id} opened at {trade.EntryTime:O}, which opens no bar of the run after the first.");

			var decided = filled - 1;

			IsTrue(signalled[decided],
				$"trade {trade.Id} filled on the bar opening {bars[filled].OpenTime:O}, whose predecessor did " +
				"not close on a price meeting the entry rule, so the rule was read off a bar that had not closed.");

			AreEqual(bars[decided].Close, trade.EntryPrice,
				$"trade {trade.Id} filled at {trade.EntryPrice}, which is not the close the decision was taken on.");

			if (bars[filled].Open != bars[decided].Close)
				separable++;
		}

		IsTrue(separable > 0,
			"every bar that filled a decision opened where the decision bar closed, so the price above could " +
			"not tell the two apart and nothing was measured.");
	}

	/// <summary>
	/// Trading is not free. The same strategy over the same bars keeps less of the result once the
	/// spread widens, which is what makes a stressed-cost run mean anything.
	/// </summary>
	[TestMethod]
	public async Task AWiderSpreadCostsTheRunMore()
	{
		var bars = Waves(600);
		var runner = new EmulatedBacktestRunner();

		var cheap = await runner.RunAsync(await RequestAsync(Oscillating(), bars, ExecutionCosts.Default), CancellationToken);

		var dear = await runner.RunAsync(
			await RequestAsync(Oscillating(), bars, ExecutionCosts.Default.Scaled(10m)),
			CancellationToken);

		var cheapNet = cheap.Trades.Sum(t => t.Net);
		var dearNet = dear.Trades.Sum(t => t.Net);

		IsTrue(cheap.Trades.Count > 0 && dear.Trades.Count > 0);
		IsTrue(dearNet < cheapNet, $"costs ten times higher left the run no worse off: {dearNet} against {cheapNet}.");

		AreEqual(cheap.Trades.Count, dear.Trades.Count,
			"raising the costs changed which trades were taken, so the two runs are not comparable.");
	}

	/// <summary>
	/// What the spread took is reported apart from what the fees took. A candidate that is marginal is
	/// marginal for one of those two reasons, and only one of them can be traded around.
	/// </summary>
	[TestMethod]
	public async Task TheSpreadAndTheFeesAreReportedApart()
	{
		var outcome = await new EmulatedBacktestRunner().RunAsync(
			await RequestAsync(Oscillating(), Waves(600), new ExecutionCosts(Fees: 0.0002m, HalfSpread: 0.01m)),
			CancellationToken);

		IsTrue(outcome.Trades.Count > 0);

		foreach (var trade in outcome.Trades)
		{
			// Twenty units changed hands over the two ends of the trade, at a cent of spread each.
			AreEqual(0.2m, Math.Round(trade.Slippage, 6), $"trade {trade.Id} was charged {trade.Slippage} of spread.");
			AreEqual(0.004m, Math.Round(trade.Commission, 6), $"trade {trade.Id} was charged {trade.Commission} of fees.");
		}
	}

	/// <summary>The same run twice gives the same trades, or nothing measured on it means anything.</summary>
	[TestMethod]
	public async Task TheSameRunTwiceGivesTheSameTrades()
	{
		var bars = Waves(400);
		var runner = new EmulatedBacktestRunner();

		var first = await runner.RunAsync(await RequestAsync(Oscillating(), bars, ExecutionCosts.Default), CancellationToken);
		var second = await runner.RunAsync(await RequestAsync(Oscillating(), bars, ExecutionCosts.Default), CancellationToken);

		AreEqual(first.Trades.Count, second.Trades.Count);

		foreach (var (a, b) in first.Trades.Zip(second.Trades))
			AreEqual(a, b, "the same candidate over the same bars produced a different trade.");
	}

	/// <summary>A strategy whose rules are never met trades nothing, and says so rather than failing.</summary>
	[TestMethod]
	public async Task AStrategyWhoseRulesAreNeverMetTradesNothing()
	{
		var unreachable = Oscillating() with
		{
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(new Field(CandleFields.Close), ComparisonOperators.GreaterThan, new Constant(1_000_000))),
			],
		};

		var outcome = await new EmulatedBacktestRunner().RunAsync(
			await RequestAsync(unreachable, Waves(300), ExecutionCosts.Default),
			CancellationToken);

		AreEqual(0, outcome.Trades.Count);
		IsTrue(outcome.Equity.Count > 0, "a run with no trades still has a starting account value.");
	}

	/// <summary>What the runner produces is what the metrics are computed from, without a step in between.</summary>
	[TestMethod]
	public async Task TheOutcomeCanBeMeasuredDirectly()
	{
		var bars = Waves(600);

		var outcome = await new EmulatedBacktestRunner().RunAsync(
			await RequestAsync(Oscillating(), bars, ExecutionCosts.Default),
			CancellationToken);

		var metrics = RunMetricsCalculator.Measure(
			outcome.Trades,
			outcome.Equity,
			100_000m,
			bars[^1].OpenTime - bars[0].OpenTime,
			outcome.ExecutionErrorCount);

		AreEqual(outcome.Trades.Count, metrics.Trades.Count);
		AreEqual(outcome.Trades.Sum(t => t.Net), metrics.Net.Profit);
		IsTrue(metrics.Costs.Total > 0, "a run that traded was charged nothing at all.");
		IsTrue(metrics.Activity.ExposurePercent > 0);
	}

	/// <summary>
	/// A run reads its own range out of the shared storage and nothing either side of it, even when the
	/// storage holds the bars before and after on the same day. A development run that saw the first bar
	/// of validation would have seen data it is not allowed.
	/// </summary>
	[TestMethod]
	public async Task ARunReadsOnlyItsOwnRangeOfTheStorage()
	{
		var bars = Waves(600);
		var whole = await RequestAsync(Oscillating(), bars, ExecutionCosts.Default);

		var from = bars[150].OpenTime;
		var to = bars[450].OpenTime;

		var outcome = await new EmulatedBacktestRunner().RunAsync(
			whole with { Bars = whole.Bars with { From = from, To = to, Count = 300 } },
			CancellationToken);

		AreEqual(300, outcome.BarsProcessed, "the run read bars outside the range it was given.");
		IsTrue(outcome.Trades.Count > 0, "nothing traded, so the range was not exercised.");
		IsTrue(outcome.Trades.All(t => t.EntryTime >= from && t.EntryTime < to),
			"a trade was entered outside the range the run was given.");
	}

	/// <summary>A run with no bars is refused rather than reported as a strategy that did nothing.</summary>
	[TestMethod]
	public async Task ARunWithoutBarsIsRefused()
	{
		var request = await RequestAsync(Oscillating(), [], ExecutionCosts.Default);

		await ThrowsAsync<ArgumentException>(
			() => new EmulatedBacktestRunner().RunAsync(request, CancellationToken));
	}

	/// <summary>
	/// Entering a candle later is a different run, and one the same strategy can be asked for.
	/// </summary>
	/// <remarks>
	/// A backtest enters at the first price the signal could have been acted on: the bar after the one
	/// the rule fired on, every time. Getting there first is the assumption a live account contradicts
	/// soonest, and nothing else here measures it: the walk-forward windows ask whether a result holds
	/// over another stretch of market, this asks whether it holds when the entry waits a bar longer.
	/// </remarks>
	[TestMethod]
	public async Task EnteringACandleLaterIsADifferentRun()
	{
		var bars = Waves(600);
		var runner = new EmulatedBacktestRunner();

		var immediate = await runner.RunAsync(await RequestAsync(Oscillating(), bars, ExecutionCosts.Default), CancellationToken);

		var delayed = await runner.RunAsync(
			(await RequestAsync(Oscillating(), bars, ExecutionCosts.Default)) with { EntryDelayBars = 1 },
			CancellationToken);

		IsTrue(immediate.Trades.Count > 0, "the immediate run traded nothing, so there is nothing to delay.");
		IsTrue(delayed.Trades.Count > 0, "delaying the entry stopped the strategy trading at all.");

		var first = immediate.Trades[0];
		var later = delayed.Trades[0];

		IsTrue(later.EntryTime > first.EntryTime,
			$"the delayed run entered at {later.EntryTime:HH:mm} and the immediate one at {first.EntryTime:HH:mm}.");
	}

	/// <summary>
	/// Bars that walk up and back down on a fixed period, inside one American session per day so the
	/// board is open for every one of them.
	/// </summary>
	/// <remarks>
	/// Each bar opens above the close before it. Flush against it, the close a decision was taken on is
	/// also the opening price of the bar that fills the decision, and a fill from either of the two is
	/// the same price.
	/// </remarks>
	/// <param name="count">How many bars to make.</param>
	/// <returns>The bars, oldest first.</returns>
	private static IReadOnlyList<Candle> Waves(int count)
	{
		const decimal gap = 0.20m;

		var bars = new List<Candle>(count);
		var time = _open;

		for (var i = 0; i < count; i++)
		{
			// Sixty bars to a full cycle: long enough that a ten-bar average lags the price by enough to
			// cross it, rather than tracking it too closely to ever be crossed.
			var wave = (decimal)Math.Sin(i * 2 * Math.PI / 60);
			var close = 100m + Math.Round(5m * wave, 2);
			var open = i == 0 ? close : bars[^1].Close + gap;

			bars.Add(new(
				time,
				open,
				Math.Max(open, close) + 0.05m,
				Math.Min(open, close) - 0.05m,
				close,
				1_000m));

			time = time.AddMinutes(5);

			// The American session runs to twenty hundred UTC; the next bar after it opens the next day.
			if (time.TimeOfDay >= TimeSpan.FromHours(20))
				time = time.Date.AddDays(time.DayOfWeek == DayOfWeek.Friday ? 3 : 1).Add(_open.TimeOfDay);
		}

		return bars;
	}

	/// <summary>
	/// Which bars close above their own ten-bar average, which is the entry rule of <see cref="Oscillating"/>
	/// worked out from the bars rather than read back out of what the run did with them.
	/// </summary>
	/// <param name="bars">The bars the run was given.</param>
	/// <returns>One flag per bar, set where that bar's close met the rule.</returns>
	private static bool[] EntriesSignalledBy(IReadOnlyList<Candle> bars)
	{
		const int length = 10;

		var signalled = new bool[bars.Count];
		var window = 0m;

		for (var i = 0; i < bars.Count; i++)
		{
			window += bars[i].Close;

			if (i >= length)
				window -= bars[i - length].Close;

			// Until the window is full the average is of fewer bars than the rule asks for, and the
			// strategy is still warming up rather than trading.
			if (i >= length - 1)
				signalled[i] = bars[i].Close > window / length;
		}

		return signalled;
	}

	/// <summary>The bar a moment opens, or minus one when it opens none of them.</summary>
	/// <param name="bars">The bars the run was given.</param>
	/// <param name="time">Moment to look for.</param>
	/// <returns>Index of the bar, or minus one.</returns>
	private static int IndexOfBarOpeningAt(IReadOnlyList<Candle> bars, DateTime time)
	{
		for (var i = 0; i < bars.Count; i++)
		{
			if (bars[i].OpenTime == time)
				return i;
		}

		return -1;
	}

	/// <summary>Buy when the price is above its own average, and let go five bars later.</summary>
	private static StrategySpec Oscillating()
		=> new()
		{
			Name = "Above its average",
			Thesis = "A price above its own recent average keeps going for a few bars.",
			AllowLong = true,
			AllowShort = false,
			TimeFrame = TimeSpan.FromMinutes(5),
			WarmupBars = 15,
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Field(CandleFields.Close),
						ComparisonOperators.GreaterThan,
						new IndicatorRef("sma", new Constant(10), CandleFields.Close))),
			],
			Exits = [new("x1", ExitKinds.TimeExit, TradeDirections.Long, Length: new Constant(5))],
			Parameters = [],
			Risk = new(0.10m, 0.02m),
		};

	private async Task<BacktestRequest> RequestAsync(StrategySpec spec, IReadOnlyList<Candle> bars, ExecutionCosts costs)
	{
		var built = new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory)).Build(spec);

		return new(
			built.Assembly,
			built.ClassName,
			new Dictionary<string, decimal>(StringComparer.Ordinal),
			"DEMO",
			TimeSpan.FromMinutes(5),
			await StoredBars.WriteAsync(bars, "DEMO", TimeSpan.FromMinutes(5), CancellationToken),
			StartingEquity: 100_000m,
			Volume: 10m,
			PriceStep: 0.01m,
			costs);
	}
}
