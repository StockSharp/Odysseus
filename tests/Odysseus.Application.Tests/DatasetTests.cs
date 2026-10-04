namespace Odysseus.Application.Tests;

/// <summary>
/// Building a dataset, and dividing it in time.
/// </summary>
[TestClass]
public class DatasetTests : OdysseusTestBase
{
	private static readonly DateTime _start = new(2025, 1, 2, 14, 30, 0, DateTimeKind.Utc);

	private static readonly TimeSpan _frame = TimeSpan.FromMinutes(5);

	/// <summary>The order bars arrive in does not change what they are.</summary>
	[TestMethod]
	public void ArrivalOrderDoesNotMatter()
	{
		var ordered = DatasetBuilder.Build(Bars(), _frame, "test", isSynthetic: true);

		var shuffled = Bars();
		shuffled["AAA"] = [.. shuffled["AAA"].Reverse()];

		var second = DatasetBuilder.Build(shuffled, _frame, "test", isSynthetic: true);

		IsTrue(ordered.Bars["AAA"].SequenceEqual(second.Bars["AAA"]), "the same bars in another order were kept differently.");
		AreEqual(ordered.Manifest.Split, second.Manifest.Split);
	}

	/// <summary>
	/// A bar whose prices cannot describe a real bar is dropped rather than repaired: a guessed price is
	/// indistinguishable from an observed one afterwards.
	/// </summary>
	[TestMethod]
	public void MalformedBarsAreDroppedAndCounted()
	{
		var bars = Bars();
		var candles = bars["AAA"].ToList();

		candles[5] = candles[5] with { Low = candles[5].High + 5m };
		candles[6] = candles[6] with { Volume = -1m };
		bars["AAA"] = candles;

		var dataset = DatasetBuilder.Build(bars, _frame, "test", isSynthetic: true);
		var quality = dataset.Manifest.Quality.Single(q => q.Symbol == "AAA");

		AreEqual(2, quality.Invalid);
		AreEqual(candles.Count - 2, quality.Records);
		IsFalse(dataset.Bars["AAA"].Any(c => !c.IsWellFormed), "a malformed bar survived into the dataset.");
	}

	/// <summary>Two bars claiming the same moment cannot both be right, so the later one is dropped.</summary>
	[TestMethod]
	public void DuplicateMomentsAreDroppedAndCounted()
	{
		var bars = Bars();
		var candles = bars["AAA"].ToList();

		// A well-formed bar claiming a moment that is already taken: the volume differs, so the two
		// disagree about the same moment without either being malformed on its own.
		candles.Add(candles[3] with { Volume = candles[3].Volume + 1m });
		bars["AAA"] = candles;

		var quality = DatasetBuilder
			.Build(bars, _frame, "test", isSynthetic: true)
			.Manifest.Quality.Single(q => q.Symbol == "AAA");

		AreEqual(1, quality.Duplicates);
	}

	/// <summary>Missing bars are reported rather than filled in.</summary>
	[TestMethod]
	public void GapsAreCounted()
	{
		var bars = Bars();
		var candles = bars["AAA"].ToList();

		candles.RemoveRange(50, 3);
		bars["AAA"] = candles;

		var quality = DatasetBuilder
			.Build(bars, _frame, "test", isSynthetic: true)
			.Manifest.Quality.Single(q => q.Symbol == "AAA");

		AreEqual(1, quality.Gaps);
		AreEqual(candles.Count, quality.Records, "a gap must be reported, not filled.");
	}

	/// <summary>
	/// Too little data is refused outright, and the refusal says how much was usable and why the rest
	/// was not — the caller has to be able to act on it.
	/// </summary>
	[TestMethod]
	public void TooLittleDataIsRefusedWithNumbers()
	{
		var bars = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.Ordinal)
		{
			["AAA"] = [.. Generate(10)],
		};

		var error = Throws<ArgumentException>(() => DatasetBuilder.Build(bars, _frame, "test", isSynthetic: true));

		IsTrue(error.Message.Contains("10 usable bars", StringComparison.Ordinal), "the refusal must say how much was usable.");
		IsTrue(error.Message.Contains(DatasetBuilder.MinimumBarsPerSymbol.ToString(), StringComparison.Ordinal),
			"the refusal must say how much is needed.");
	}

	/// <summary>Generated data stays marked as generated wherever it goes.</summary>
	[TestMethod]
	public void SyntheticDataIsMarked()
	{
		IsTrue(DemoDataset.Build(barsPerSymbol: 500).Manifest.IsSynthetic,
			"a dataset of invented prices must say so, or a plausible equity curve over it reads as evidence.");
	}

	/// <summary>The demo dataset is reproducible from its seed.</summary>
	[TestMethod]
	public void DemoDataIsReproducible()
	{
		static IReadOnlyList<Candle> Generated(int seed) => DemoDataset.Build(seed: seed, barsPerSymbol: 300).Bars["DEMO1"];

		IsTrue(Generated(7).SequenceEqual(Generated(7)), "the same seed generated different bars.");
		IsFalse(Generated(7).SequenceEqual(Generated(8)), "different seeds generated the same bars.");
	}

	/// <summary>The slices are contiguous, ordered in time, and none of them is empty.</summary>
	[TestMethod]
	public void SlicesTileTheRangeInOrder()
	{
		var split = DatasetSplit.Create(_start, _start.AddDays(100), 0.6, 0.2);

		var development = split.BoundsOf(DataSlices.Development);
		var validation = split.BoundsOf(DataSlices.Validation);
		var final = split.BoundsOf(DataSlices.Final);

		AreEqual(development.To, validation.From, "the slices must be contiguous.");
		AreEqual(validation.To, final.From, "the slices must be contiguous.");

		IsTrue(development.From < development.To);
		IsTrue(validation.From < validation.To);
		IsTrue(final.From < final.To);

		AreEqual(DataSlices.Development, split.SliceOf(_start));
		AreEqual(DataSlices.Validation, split.SliceOf(development.To));
		AreEqual(DataSlices.Final, split.SliceOf(validation.To));
	}

	/// <summary>A split that keeps nothing closed is refused, and the refusal says why it matters.</summary>
	[TestMethod]
	public void ASplitMustKeepSomethingClosed()
	{
		var error = Throws<ArgumentOutOfRangeException>(
			() => DatasetSplit.Create(_start, _start.AddDays(10), 0.8, 0.2));

		IsTrue(error.Message.Contains("closed", StringComparison.Ordinal));
	}

	/// <summary>A moment outside the data belongs to no slice, rather than to the nearest one.</summary>
	[TestMethod]
	public void MomentsOutsideTheDataBelongNowhere()
	{
		var split = DatasetSplit.Create(_start, _start.AddDays(100), 0.6, 0.2);

		Throws<ArgumentOutOfRangeException>(() => split.SliceOf(_start.AddDays(-1)));
		Throws<ArgumentOutOfRangeException>(() => split.SliceOf(_start.AddDays(100)));
	}

	private static Dictionary<string, IReadOnlyList<Candle>> Bars()
		=> new(StringComparer.Ordinal)
		{
			["AAA"] = [.. Generate(300)],
			["BBB"] = [.. Generate(300, 50m)],
		};

	private static IEnumerable<Candle> Generate(int count, decimal start = 100m)
	{
		var price = start;
		var time = _start;

		for (var i = 0; i < count; i++)
		{
			var close = price + (i % 7 - 3) * 0.1m;

			yield return new(time, price, Math.Max(price, close) + 0.2m, Math.Min(price, close) - 0.2m, close, 1000 + i);

			price = close;
			time += _frame;
		}
	}
}
