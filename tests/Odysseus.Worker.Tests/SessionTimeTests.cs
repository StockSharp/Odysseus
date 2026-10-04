namespace Odysseus.Worker.Tests;

/// <summary>
/// What time of day a rule about the session means.
/// </summary>
/// <remarks>
/// A specification that says nothing before ten to ten means ten to ten where the market is. Bars are
/// stored in UTC, so read literally that rule fires at five to six in the morning New York time in the
/// summer and at ten to five in the winter — hours before the opening bell, in a stretch of the tape
/// that barely trades. Nothing about the resulting numbers looks wrong, and the strategy that runs for
/// real does something else entirely.
///
/// The offset is not a constant either. New York is five hours behind in the winter and four in the
/// summer, so a rule written once has to mean the same thing on both sides of the changeover.
/// </remarks>
[TestClass]
public class SessionTimeTests : OdysseusTestBase
{
	private static readonly TimeSpan _opens = new(9, 50, 0);
	private static readonly TimeSpan _closes = new(15, 0, 0);
	private static readonly TimeSpan _timeFrame = TimeSpan.FromMinutes(5);

	/// <summary>Winter, when New York is five hours behind.</summary>
	private static readonly DateTime _january = new(2026, 1, 14, 0, 0, 0, DateTimeKind.Utc);

	/// <summary>Summer, when it is four.</summary>
	private static readonly DateTime _july = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

	/// <summary>Every position is opened inside the window, on the clock the market keeps.</summary>
	/// <remarks>
	/// The window is a rule about the candle the decision is taken on, and an order sent when a candle
	/// closes fills on the candle after it. The last decision the window allows is therefore filled one
	/// candle past the end of the window. That is a fill one candle late, not a rule read in the wrong
	/// zone: the mistake this is here to catch is hours wide.
	/// </remarks>
	[TestMethod]
	[DataRow("january")]
	[DataRow("july")]
	public async Task PositionsAreOpenedOnTheMarketsClock(string season)
	{
		var day = season == "january" ? _january : _july;

		var outcome = await RunAsync(Bars(day));

		IsTrue(outcome.Trades.Count > 0, "nothing traded at all, so the window cannot be checked.");

		var latest = _closes + _timeFrame;

		foreach (var trade in outcome.Trades)
		{
			var local = MarketTime(trade.EntryTime);

			IsTrue(local >= _opens && local <= latest,
				$"a position was opened at {local} market time, outside the window the specification asked for; " +
				$"the bar was {trade.EntryTime:HH:mm} UTC.");
		}
	}

	/// <summary>
	/// The same rule means the same hour in both seasons, though the offset between them differs.
	/// </summary>
	[TestMethod]
	public async Task TheRuleMeansTheSameHourInBothSeasons()
	{
		var winter = await RunAsync(Bars(_january));
		var summer = await RunAsync(Bars(_july));

		var first = winter.Trades.Select(t => MarketTime(t.EntryTime)).Min();
		var second = summer.Trades.Select(t => MarketTime(t.EntryTime)).Min();

		IsTrue(first >= _opens && second >= _opens,
			$"the earliest entry was {first} in winter and {second} in summer, against a window opening at {_opens}.");

		IsTrue(Math.Abs((first - second).TotalHours) < 1,
			$"the same rule opened at {first} in one season and {second} in the other.");
	}

	private static TimeSpan MarketTime(DateTime utc)
		=> TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), MarketSession.Zone).TimeOfDay;

	/// <summary>Buy above the average of the last three bars, but only inside the session window.</summary>
	private static StrategySpec Spec()
		=> new()
		{
			Name = "Inside the session",
			Thesis = "A price above its own recent average keeps going, but only while the session is on.",
			AllowLong = true,
			AllowShort = false,
			TimeFrame = _timeFrame,
			WarmupBars = 10,
			Entries =
			[
				new("e1", TradeDirections.Long,
					new AllOf(
					[
						new Compare(
							new Field(CandleFields.Close),
							ComparisonOperators.GreaterThan,
							new IndicatorRef("sma", new Constant(3), CandleFields.Close)),
						new SessionWindow("09:50", "15:00"),
					])),
			],
			Exits = [new("x1", ExitKinds.TimeExit, TradeDirections.Long, Length: new Constant(2))],
			Parameters = [],
			Risk = new(0.10m, 0.02m),
		};

	/// <summary>
	/// Twenty days of bars covering the whole clock, so a rule read in the wrong zone still finds plenty
	/// to trade on and gives itself away by when it traded rather than by trading nothing.
	/// </summary>
	private static IReadOnlyList<Candle> Bars(DateTime from)
	{
		var bars = new List<Candle>();
		var time = from;

		for (var i = 0; i < 20 * 24 * 12; i++)
		{
			var wave = (decimal)Math.Sin(i * 2 * Math.PI / 40);
			var close = 100m + Math.Round(5m * wave, 2);
			var open = bars.Count == 0 ? close : bars[^1].Close;

			bars.Add(new(time, open, Math.Max(open, close) + 0.05m, Math.Min(open, close) - 0.05m, close, 10_000m));

			time = time.AddMinutes(5);
		}

		return bars;
	}

	private async Task<BacktestOutcome> RunAsync(IReadOnlyList<Candle> bars)
	{
		var built = new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory)).Build(Spec());

		return await new EmulatedBacktestRunner().RunAsync(
			new(
				built.Assembly,
				built.ClassName,
				new Dictionary<string, decimal>(),
				"DEMO",
				_timeFrame,
				await StoredBars.WriteAsync(bars, "DEMO", _timeFrame, CancellationToken),
				StartingEquity: 100_000m,
				Volume: 1m,
				PriceStep: 0.01m,
				ExecutionCosts.Default),
			CancellationToken);
	}
}
