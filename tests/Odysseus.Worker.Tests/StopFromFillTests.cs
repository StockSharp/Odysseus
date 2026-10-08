namespace StockSharp.Odysseus.Worker.Tests;

/// <summary>
/// Where a stop is measured from: the price the position was actually bought at.
/// </summary>
/// <remarks>
/// With the entry taken a bar after the signal, the fill and the close the signal was read on are
/// different prices, so a stop measured from the wrong one is placed somewhere else and fires on another
/// bar. The bars are built so that every bar's true range is exactly one and every close sits on a grid
/// of 0.4, which makes the bar the stop must fire on a matter of arithmetic.
/// </remarks>
[TestClass]
public class StopFromFillTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);
	private static readonly TimeSpan _timeFrame = TimeSpan.FromMinutes(5);

	private const decimal Step = 0.4m;

	/// <summary>
	/// A stop one average range below the entry fires on the first bar that closes at least one below the
	/// price that was paid, not below the close the signal was read on.
	/// </summary>
	[TestMethod]
	public async Task TheStopIsMeasuredFromThePricePaid()
	{
		var bars = Bars();
		var range = await StoredBars.WriteAsync(bars, "DEMO", _timeFrame, CancellationToken);
		var built = new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory)).Build(Spec());

		BacktestRequest Request(int delay)
			=> new(built.Assembly, built.ClassName, new Dictionary<string, decimal>(StringComparer.Ordinal), "DEMO",
				_timeFrame, range, 100_000m, 10m, 0.01m, ExecutionCosts.Default, delay);

		var prompt = (await new EmulatedBacktestRunner().RunAsync(Request(0), CancellationToken)).Trades[0];
		var delayed = (await new EmulatedBacktestRunner().RunAsync(Request(1), CancellationToken)).Trades[0];

		// The setup this test depends on: one bar later on a rising price is a dearer fill.
		AreEqual(prompt.EntryPrice + Step, delayed.EntryPrice, "the delayed entry did not fill a bar later.");

		// One below the price paid falls between two closes of the grid, so the stop fires on the lower one.
		// Measured from the signal's close it would wait for the next one down.
		AreEqual(delayed.EntryPrice - 3 * Step, delayed.ExitPrice,
			"the stop fired somewhere other than one average range below the price that was paid.");

		IsTrue(bars.Any(b => b.Close == delayed.ExitPrice && b.OpenTime > delayed.EntryTime),
			"the exit price is not the close of a bar after the entry.");
	}

	// A steady rise and then a steady fall, every bar opening at the last close and spanning exactly one,
	// so the true range of every bar is one and so is any average of it.
	private static IReadOnlyList<Candle> Bars()
	{
		var bars = new List<Candle>();
		var close = 100m;

		for (var i = 0; i < 120; i++)
		{
			var open = close;
			var delta = i < 60 ? Step : -Step;

			close = open + delta;

			var pad = (1m - Math.Abs(delta)) / 2;

			bars.Add(new(_open + _timeFrame * i, open, Math.Max(open, close) + pad, Math.Min(open, close) - pad, close, 10_000m));
		}

		return bars;
	}

	private static StrategySpec Spec()
		=> new()
		{
			Name = "Above its average",
			Thesis = "A price above its own recent average keeps going.",
			AllowLong = true,
			AllowShort = false,
			TimeFrame = _timeFrame,
			WarmupBars = 15,
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Field(CandleFields.Close),
						ComparisonOperators.GreaterThan,
						new IndicatorRef("sma", new Constant(10), CandleFields.Close))),
			],
			Exits = [new("x1", ExitKinds.AtrStop, TradeDirections.Long, Length: new Constant(5), Multiplier: new Constant(1))],
			Parameters = [],
			Risk = new(0.10m, 0.02m),
		};
}
