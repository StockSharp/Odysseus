namespace StockSharp.Odysseus.Worker.Tests;

/// <summary>
/// Which bar a decision is taken on, and which bar pays for it.
/// </summary>
/// <remarks>
/// A run over a whole wave of bars cannot answer this: when most bars meet the entry rule, a fill one
/// bar early still lands on a bar that meets it, and the run looks right whichever bar it read. The bars
/// here are built so that exactly one of them meets the rule and its neighbours plainly do not, and so
/// that the two candidate bars average different prices. Then the fill names the bar it came from twice
/// over — by when it happened and by what it cost.
///
/// A strategy that acted on a bar before the bar had closed would read a close that had not happened and
/// buy inside the bar that produced its own signal, and every number the product reports would be earned
/// that way. The order is sent when the bar closes and is filled at the price the market stood at then,
/// so its time is the start of the next bar and its price is the close the decision was taken on.
/// </remarks>
[TestClass]
public class DecisionTimingTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);
	private static readonly TimeSpan _timeFrame = TimeSpan.FromMinutes(5);

	/// <summary>The one bar that meets the entry rule is not the bar the entry is filled on.</summary>
	[TestMethod]
	public async Task TheBarThatSignalsIsNotTheBarThatFills()
	{
		const int signals = 30;

		var bars = OneBarAbove(signals, 60);

		var outcome = await new EmulatedBacktestRunner().RunAsync(
			await RequestAsync(Oscillating(warmup: 15), bars),
			CancellationToken);

		AreEqual(1, outcome.Trades.Count,
			"one bar of these meets the entry rule, so the run should hold exactly one trade.");

		var trade = outcome.Trades[0];
		var decided = bars[signals];
		var filled = bars[signals + 1];

		AreEqual(filled.OpenTime, trade.EntryTime,
			$"the rule was met on the bar opening {decided.OpenTime:HH:mm}, and the order it sent should have " +
			$"been filled on the next bar; it was filled at {trade.EntryTime:HH:mm}.");

		AreEqual(decided.Close, trade.EntryPrice,
			"a market order sent at a bar's close should have been filled at the price the market stood at then.");

		AreNotEqual((decided.High + decided.Low) / 2m, trade.EntryPrice,
			"the entry was priced from the bar whose close produced the signal, so the run bought inside a bar " +
			"it should not have seen the end of yet.");
	}

	/// <summary>
	/// Warm-up is counted in bars that have closed, and the bar it ends on is decided rather than filled.
	/// </summary>
	/// <remarks>
	/// The bars rise steadily, so every bar past the tenth meets the entry rule and only the warm-up can
	/// hold the first trade back. A run that filled on the bar it decided on would enter one bar earlier
	/// than each of these, which is what the count is asked for here rather than merely varied.
	/// </remarks>
	[TestMethod]
	[DataRow(15)]
	[DataRow(20)]
	[DataRow(30)]
	public async Task TheFirstFillLandsOnTheBarAfterTheWarmupEnds(int warmup)
	{
		var bars = Rising(60);

		var outcome = await new EmulatedBacktestRunner().RunAsync(
			await RequestAsync(Oscillating(warmup), bars),
			CancellationToken);

		IsTrue(outcome.Trades.Count > 0, "the rule was met on every bar past the warm-up and nothing traded.");

		var first = outcome.Trades[0];
		var filled = bars[warmup];

		AreEqual(filled.OpenTime, first.EntryTime,
			$"a warm-up of {warmup} bars is over once that many have closed, which is the bar opening " +
			$"{bars[warmup - 1].OpenTime:HH:mm}; the order it sent should have filled on the next bar, and the " +
			$"run entered at {first.EntryTime:HH:mm}.");

		AreEqual(bars[warmup - 1].Close, first.EntryPrice,
			"a market order sent at a bar's close should have been filled at the price the market stood at then.");
	}

	/// <summary>
	/// A flat price with a single bar closing well above it, so that bar alone closes above its own
	/// ten-bar average.
	/// </summary>
	/// <remarks>
	/// The bar that signals and the bar after it average different prices, and both differ from what the
	/// flat bars around them average, so the price an entry was filled at says which bar filled it. Every
	/// bar opens where the one before it closed.
	/// </remarks>
	/// <param name="signals">Index of the bar that meets the rule.</param>
	/// <param name="count">How many bars to make.</param>
	/// <returns>The bars, oldest first.</returns>
	private static IReadOnlyList<Candle> OneBarAbove(int signals, int count)
	{
		var bars = new List<Candle>(count);
		var time = _open;

		for (var i = 0; i < count; i++)
		{
			// Flat at a hundred, which no ten-bar average is ever exceeded by; the spike and the bar that
			// takes the price back down average a hundred and five and a hundred and two.
			var candle = i == signals
				? new Candle(time, 100m, 110m, 100m, 110m, 1_000m)
				: i == signals + 1
					? new Candle(time, 110m, 110m, 94m, 100m, 1_000m)
					: new Candle(time, 100m, 100.05m, 99.95m, 100m, 1_000m);

			bars.Add(candle);
			time = time.AddMinutes(5);
		}

		return bars;
	}

	/// <summary>Bars that climb by a tenth each time, so every one of them closes above its own average.</summary>
	/// <param name="count">How many bars to make.</param>
	/// <returns>The bars, oldest first.</returns>
	private static IReadOnlyList<Candle> Rising(int count)
	{
		var bars = new List<Candle>(count);
		var time = _open;

		for (var i = 0; i < count; i++)
		{
			var close = 100m + 0.1m * i;
			var open = i == 0 ? close : bars[^1].Close;

			bars.Add(new(time, open, close + 0.05m, open - 0.05m, close, 1_000m));
			time = time.AddMinutes(5);
		}

		return bars;
	}

	/// <summary>Buy when the price is above its own average, and let go five bars later.</summary>
	/// <param name="warmup">Bars that must close before anything is decided.</param>
	/// <returns>The specification.</returns>
	private static StrategySpec Oscillating(int warmup)
		=> new()
		{
			Name = "Above its average",
			Thesis = "A price above its own recent average keeps going for a few bars.",
			AllowLong = true,
			AllowShort = false,
			TimeFrame = _timeFrame,
			WarmupBars = warmup,
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

	private async Task<BacktestRequest> RequestAsync(StrategySpec spec, IReadOnlyList<Candle> bars)
	{
		var built = new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory)).Build(spec);

		return new(
			built.Assembly,
			built.ClassName,
			new Dictionary<string, decimal>(StringComparer.Ordinal),
			"DEMO",
			_timeFrame,
			await StoredBars.WriteAsync(bars, "DEMO", _timeFrame, CancellationToken),
			StartingEquity: 100_000m,
			Volume: 10m,
			PriceStep: 0.01m,
			ExecutionCosts.Default);
	}
}
