namespace Odysseus.Application.Tests;

/// <summary>
/// Where a dataset is divided: by fixed shares of its range, the same for every dataset.
/// </summary>
[TestClass]
public class SplitTests : OdysseusTestBase
{
	private static readonly DateTime _start = new(2025, 3, 3, 14, 30, 0, DateTimeKind.Utc);

	private static readonly TimeSpan _frame = TimeSpan.FromMinutes(5);

	// Restated rather than read from the code under test, because a test that imports the number it is
	// checking checks nothing.
	private const double Development = 0.6;
	private const double Validation = 0.2;

	/// <summary>Development takes three fifths of the range, validation one fifth, the rest is closed.</summary>
	[TestMethod]
	public void EveryDatasetIsDividedByTheSameShares()
	{
		var split = DatasetBuilder.Build(Bars(seed: 1), _frame, "test", isSynthetic: false).Manifest.Split;
		var span = split.To - split.From;

		AreEqual(split.From + span * Development, split.DevelopmentTo);
		AreEqual(split.From + span * (Development + Validation), split.ValidationTo);
	}

	/// <summary>
	/// What the bars say does not move the division: two datasets over the same range divide at the same
	/// moments, so importing a range again lands every run on the slices it was measured on before.
	/// </summary>
	[TestMethod]
	public void TheSameRangeDividesTheSameWayWhateverItHolds()
	{
		var first = DatasetBuilder.Build(Bars(seed: 1), _frame, "test", isSynthetic: false).Manifest;
		var second = DatasetBuilder.Build(Bars(seed: 2), _frame, "other", isSynthetic: false).Manifest;

		AreEqual(first.Split, second.Split);
		AreNotEqual(first.Id, second.Id, "two imports were given one identity.");
	}

	/// <summary>Every slice holds bars, and the closed one is the last.</summary>
	[TestMethod]
	public void EveryDivisionLeavesBarsClosed()
	{
		var dataset = DatasetBuilder.Build(Bars(seed: 3), _frame, "test", isSynthetic: false);
		var split = dataset.Manifest.Split;
		var bars = dataset.Bars["AAA"];

		foreach (var slice in Enum.GetValues<DataSlices>())
		{
			var (from, to) = split.BoundsOf(slice);

			IsTrue(bars.Any(b => b.OpenTime >= from && b.OpenTime < to), $"the {slice} slice holds no bars.");
		}

		AreEqual(DataSlices.Final, split.SliceOf(bars[^1].OpenTime));
	}

	private static Dictionary<string, IReadOnlyList<Candle>> Bars(int seed)
	{
		var random = new Random(seed);
		var bars = new List<Candle>();
		var price = 100m;

		for (var i = 0; i < 300; i++)
		{
			var close = Math.Round(price + (decimal)(random.NextDouble() - 0.5), 2);

			bars.Add(new(_start + _frame * i, price, Math.Max(price, close) + 0.1m, Math.Min(price, close) - 0.1m, close, 1_000m));

			price = close;
		}

		return new(StringComparer.Ordinal) { ["AAA"] = bars };
	}
}
