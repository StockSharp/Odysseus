namespace StockSharp.Odysseus.Application;

using System.Linq;

/// <summary>
/// What came out of importing a set of bars.
/// </summary>
/// <param name="Manifest">The description of what was kept.</param>
/// <param name="Bars">The bars that survived the checks, by symbol.</param>
public sealed record ImportedDataset(
	DatasetManifest Manifest,
	IReadOnlyDictionary<string, IReadOnlyList<Candle>> Bars);

/// <summary>
/// Turns raw bars into a dataset, refusing what cannot be measured honestly.
/// </summary>
/// <remarks>
/// Bad bars are dropped rather than repaired: a guessed price is indistinguishable from a real one
/// afterwards, and it would be quoted in a report as though it had been observed.
/// </remarks>
public static class DatasetBuilder
{
	/// <summary>Fewest bars a symbol must contribute for the data to be worth measuring on.</summary>
	public const int MinimumBarsPerSymbol = 100;

	/// <summary>
	/// Builds a dataset from raw bars.
	/// </summary>
	/// <param name="bars">Bars by symbol, in any order.</param>
	/// <param name="timeFrame">Length of one candle.</param>
	/// <param name="source">Identity of the source the bars came through.</param>
	/// <param name="isSynthetic">Whether the bars were generated rather than observed.</param>
	/// <returns>The dataset.</returns>
	/// <exception cref="ArgumentException">No symbol contributed enough usable bars.</exception>
	/// <remarks>
	/// The range is divided by the same shares every time, so the same range always divides the same way
	/// and anybody can see where each slice lies. What keeps the closed slice closed is not where it is,
	/// but that no tool hands its bars out and that it can be measured against only once.
	/// </remarks>
	public static ImportedDataset Build(
		IReadOnlyDictionary<string, IReadOnlyList<Candle>> bars,
		TimeSpan timeFrame,
		string source,
		bool isSynthetic)
	{
		ArgumentNullException.ThrowIfNull(bars);
		ArgumentException.ThrowIfNullOrWhiteSpace(source);

		if (timeFrame <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(timeFrame), timeFrame, "A candle has a positive length.");

		if (bars.Count == 0)
			throw new ArgumentException("A dataset needs at least one symbol.", nameof(bars));

		var kept = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.Ordinal);
		var quality = new List<SymbolQuality>();

		foreach (var (symbol, raw) in bars.OrderBy(p => p.Key, StringComparer.Ordinal))
		{
			var (candles, report) = Clean(symbol, raw, timeFrame);

			if (candles.Count < MinimumBarsPerSymbol)
			{
				throw new ArgumentException(
					$"Symbol '{symbol}' contributed {candles.Count} usable bars out of {raw.Count}; " +
					$"at least {MinimumBarsPerSymbol} are needed before anything measured on it means much. " +
					$"Rejected: {report.Invalid} malformed, {report.Duplicates} duplicated.",
					nameof(bars));
			}

			kept.Add(symbol, candles);
			quality.Add(report);
		}

		var from = quality.Min(q => q.From);
		var to = quality.Max(q => q.To) + timeFrame;
		var split = DatasetSplit.Divide(from, to);

		return new(
			new DatasetManifest
			{
				Id = DatasetId.New(),
				Symbols = [.. kept.Keys],
				TimeFrame = timeFrame,
				Source = source,
				IsSynthetic = isSynthetic,
				Quality = quality,
				Split = split,
			},
			kept);
	}

	/// <summary>Narrowest the development slice may be, as a share of the whole range.</summary>
	private static (IReadOnlyList<Candle> Candles, SymbolQuality Report) Clean(
		string symbol,
		IReadOnlyList<Candle> raw,
		TimeSpan timeFrame)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
		ArgumentNullException.ThrowIfNull(raw);

		var seen = new HashSet<DateTime>();
		var kept = new List<Candle>(raw.Count);
		var duplicates = 0;
		var invalid = 0;

		foreach (var candle in raw.OrderBy(c => c.OpenTime))
		{
			if (candle.OpenTime.Kind != DateTimeKind.Utc)
				throw new ArgumentException($"Bar times of '{symbol}' must be UTC.", nameof(raw));

			if (!candle.IsWellFormed)
			{
				invalid++;
				continue;
			}

			// A repeated timestamp means two sources disagree about the same moment, and there is no way
			// to tell which is right, so the later one is dropped rather than silently preferred.
			if (!seen.Add(candle.OpenTime))
			{
				duplicates++;
				continue;
			}

			kept.Add(candle);
		}

		var gaps = 0;

		for (var i = 1; i < kept.Count; i++)
		{
			var expected = kept[i - 1].OpenTime + timeFrame;

			if (kept[i].OpenTime > expected)
				gaps++;
		}

		var report = new SymbolQuality(
			symbol,
			kept.Count,
			kept.Count > 0 ? kept[0].OpenTime : default,
			kept.Count > 0 ? kept[^1].OpenTime : default,
			gaps,
			duplicates,
			invalid);

		return (kept, report);
	}
}
