namespace Odysseus.Worker.Tests;

/// <summary>
/// Searching the numbers a specification declared.
/// </summary>
/// <remarks>
/// What matters here is not that the search finds a good setting — on made-up bars there may be none —
/// but that it searches at all, that it searches only inside the bounds the specification declared, and
/// that asking twice gives the same answer. A search nobody can repeat produces a number that happened
/// rather than a result.
/// </remarks>
[TestClass]
public class GeneticStrategyOptimizerTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 0, 0, DateTimeKind.Utc);

	/// <summary>
	/// A walk-forward fits each window on its own stretch and tests what it chose on the stretch after it:
	/// the windows follow one another, every choice is a value the specification allowed, and every trade
	/// a window reports happened inside the stretch it was tested on.
	/// </summary>
	[TestMethod]
	public async Task AWalkForwardTestsEachChoiceOnTheStretchAfterIt()
	{
		var search = await RequestAsync();

		var request = new WalkForwardRequest(
			search.Assembly,
			search.ClassName,
			search.Symbol,
			search.TimeFrame,
			search.Bars,
			search.StartingEquity,
			search.Volume,
			search.PriceStep,
			search.Costs,
			InSample: TimeSpan.FromDays(3),
			OutOfSample: TimeSpan.FromDays(1));

		var windows = await new GeneticStrategyOptimizer(batchSize: 4).WalkForwardAsync(request, CancellationToken);

		IsTrue(windows.Count > 1, $"a slice of {search.Bars.Count} bars produced {windows.Count} window(s).");

		for (var i = 0; i < windows.Count; i++)
		{
			var window = windows[i];

			AreEqual(window.InSampleTo, window.OutOfSampleFrom, "a window was not tested on the stretch right after the one it was fitted on.");
			AreEqual(TimeSpan.FromDays(3), window.InSampleTo - window.InSampleFrom);
			AreEqual(TimeSpan.FromDays(1), window.OutOfSampleTo - window.OutOfSampleFrom);

			if (i > 0)
				AreEqual(windows[i - 1].InSampleFrom.AddDays(1), window.InSampleFrom, "the windows do not step by the tested stretch.");

			var length = window.Parameters["Length"];

			IsTrue(length is >= 5m and <= 40m && length % 5m == 0m, $"window {i} chose a length of {length}, which the specification does not allow.");

			foreach (var trade in window.OutOfSample.Trades)
			{
				IsTrue(trade.EntryTime >= window.OutOfSampleFrom && trade.EntryTime < window.OutOfSampleTo,
					$"window {i} reports a trade entered at {trade.EntryTime:O}, outside the stretch it was tested on.");
			}
		}
	}

	/// <summary>The search really varies the numbers, and every setting it tries is one it was allowed.</summary>
	[TestMethod]
	public async Task TheSearchStaysInsideTheDeclaredBounds()
	{
		var trials = await new GeneticStrategyOptimizer(batchSize: 4).SearchAsync(await RequestAsync(), CancellationToken);

		IsTrue(trials.Count > 1, $"the search evaluated {trials.Count} setting(s).");

		foreach (var trial in trials)
		{
			IsTrue(trial.Parameters.ContainsKey("Length"), "the declared number was not among those tried.");

			var length = trial.Parameters["Length"];

			IsTrue(length >= 5 && length <= 40, $"the search tried a length of {length}, outside the declared 5 to 40.");
		}

		IsTrue(trials.Select(t => t.Parameters["Length"]).Distinct().Count() > 1,
			"every setting the search tried was the same one, so nothing was searched.");
	}

	/// <summary>
	/// The same seed gives the same search. Without this a project re-run lands on a different setting
	/// and no result it records can be arrived at twice.
	/// </summary>
	[TestMethod]
	public async Task TheSameSeedGivesTheSameSearch()
	{
		var optimizer = new GeneticStrategyOptimizer(batchSize: 4);

		var first = await optimizer.SearchAsync(await RequestAsync(seed: 7), CancellationToken);
		var second = await optimizer.SearchAsync(await RequestAsync(seed: 7), CancellationToken);

		AreEqual(first.Count, second.Count, "the same seed evaluated a different number of settings.");

		foreach (var (a, b) in first.Zip(second))
		{
			AreEqual(a.Parameters["Length"], b.Parameters["Length"], "the same seed tried a different setting.");
			AreEqual(a.NetProfit, b.NetProfit, "the same setting produced a different result.");
		}
	}

	/// <summary>Results come back best first, so the caller does not have to know how they were scored.</summary>
	[TestMethod]
	public async Task ResultsArriveBestFirst()
	{
		var trials = await new GeneticStrategyOptimizer(batchSize: 4).SearchAsync(await RequestAsync(), CancellationToken);

		var scores = trials.Select(t => t.Fitness).ToArray();

		IsTrue(scores.SequenceEqual(scores.OrderByDescending(s => s)), "the settings did not come back in order.");
	}

	/// <summary>
	/// A setting that traded too little scores nothing. Two trades can produce any ratio at all, and a
	/// search able to win that way will find the pair that did.
	/// </summary>
	[TestMethod]
	public async Task ASettingWithTooFewTradesScoresNothing()
	{
		var trials = await new GeneticStrategyOptimizer(batchSize: 4).SearchAsync(await RequestAsync(), CancellationToken);

		foreach (var trial in trials.Where(t => t.Trades < 30))
			AreEqual(0m, trial.Fitness, $"a setting with {trial.Trades} trades scored {trial.Fitness}.");
	}

	/// <summary>
	/// A candidate whose rules carry no number to vary is refused rather than searched pointlessly. It
	/// is a fine candidate — it just has nothing for a search to do, and spending the allowance to
	/// discover that is worse than saying so.
	/// </summary>
	[TestMethod]
	public async Task ACandidateWithNothingToVaryIsRefused()
	{
		var withoutNumbers = Searchable() with
		{
			Parameters = [],
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Field(CandleFields.Close),
						ComparisonOperators.GreaterThan,
						new IndicatorRef("sma", new Constant(10), CandleFields.Close))),
			],
		};

		var built = new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory)).Build(withoutNumbers);

		var request = (await RequestAsync()) with { Assembly = built.Assembly, ClassName = built.ClassName };

		var refusal = await ThrowsAsync<ArgumentException>(
			() => new GeneticStrategyOptimizer(batchSize: 4).SearchAsync(request, CancellationToken));

		IsTrue(refusal.Message.Contains("nothing to search", StringComparison.Ordinal),
			$"the refusal does not say why: {refusal.Message}");
	}

	/// <summary>Buy above the average of the last N bars, let go five bars later; N is what the search varies.</summary>
	private static StrategySpec Searchable()
		=> new()
		{
			Name = "Above its average",
			Thesis = "A price above its own recent average keeps going for a few bars.",
			AllowLong = true,
			AllowShort = false,
			TimeFrame = TimeSpan.FromMinutes(5),
			WarmupBars = 45,
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Field(CandleFields.Close),
						ComparisonOperators.GreaterThan,
						new IndicatorRef("sma", new ParameterRef("Length"), CandleFields.Close))),
			],
			Exits = [new("x1", ExitKinds.TimeExit, TradeDirections.Long, Length: new Constant(5))],
			Parameters = [new("Length", ParameterTypes.Integer, Default: 10, Minimum: 5, Maximum: 40, Step: 5)],
			Risk = new(0.10m, 0.02m),
		};

	/// <summary>Bars that walk up and back down on a fixed period, inside one American session per day.</summary>
	private static IReadOnlyList<Candle> Waves(int count)
	{
		var bars = new List<Candle>(count);
		var time = _open;

		for (var i = 0; i < count; i++)
		{
			var wave = (decimal)Math.Sin(i * 2 * Math.PI / 60);
			var close = 100m + Math.Round(5m * wave, 2);
			var open = i == 0 ? close : bars[^1].Close;

			bars.Add(new(
				time,
				open,
				Math.Max(open, close) + 0.05m,
				Math.Min(open, close) - 0.05m,
				close,
				1_000m));

			time = time.AddMinutes(5);

			if (time.TimeOfDay >= TimeSpan.FromHours(20))
				time = time.Date.AddDays(time.DayOfWeek == DayOfWeek.Friday ? 3 : 1).Add(_open.TimeOfDay);
		}

		return bars;
	}

	private async Task<OptimizationRequest> RequestAsync(int seed = 42, int population = 6, int generations = 2)
	{
		var built = new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory)).Build(Searchable());

		return new(
			built.Assembly,
			built.ClassName,
			"DEMO",
			TimeSpan.FromMinutes(5),
			await StoredBars.WriteAsync(Waves(600), "DEMO", TimeSpan.FromMinutes(5), CancellationToken),
			StartingEquity: 100_000m,
			Volume: 10m,
			PriceStep: 0.01m,
			ExecutionCosts.Default,
			population,
			generations,
			seed);
	}
}
