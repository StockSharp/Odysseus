namespace Odysseus.Application;

using System;
using System.Collections.Generic;

using Odysseus.Domain;

/// <summary>
/// A generated dataset that lets the whole pipeline run without a broker account.
/// </summary>
/// <remarks>
/// These bars are produced by a seeded random walk. They are here so that compilation, execution,
/// measurement and reporting can be exercised end to end on a machine with no credentials — never so
/// that a strategy can be judged. Every dataset built from them carries a synthetic flag that follows
/// it into every report, because a plausible equity curve over invented prices is the most persuasive
/// worthless result a research tool can produce.
/// </remarks>
public static class DemoDataset
{
	/// <summary>Name recorded as the source of the generated bars.</summary>
	public const string Source = "odysseus-demo-synthetic";

	/// <summary>
	/// Generates the demo dataset.
	/// </summary>
	/// <param name="seed">Seed of the walk, so the same seed gives the same bars.</param>
	/// <param name="symbols">Symbols to generate.</param>
	/// <param name="barsPerSymbol">Bars to generate for each symbol.</param>
	/// <param name="timeFrame">Length of one candle.</param>
	/// <param name="start">Moment the first candle opens, in UTC.</param>
	/// <returns>The dataset.</returns>
	public static ImportedDataset Build(
		int seed = 20260826,
		IReadOnlyList<string> symbols = null,
		int barsPerSymbol = 4000,
		TimeSpan? timeFrame = null,
		DateTime? start = null)
	{
		symbols ??= ["DEMO1", "DEMO2"];

		var frame = timeFrame ?? TimeSpan.FromMinutes(5);
		var first = start ?? new DateTime(2025, 1, 2, 14, 30, 0, DateTimeKind.Utc);

		if (first.Kind != DateTimeKind.Utc)
			throw new ArgumentException("The demo dataset starts at a UTC moment.", nameof(start));

		var bars = new Dictionary<string, IReadOnlyList<Candle>>(StringComparer.Ordinal);

		for (var s = 0; s < symbols.Count; s++)
		{
			// A separate stream per symbol, derived from the one seed, so symbols differ from each other
			// while the whole set stays reproducible from that seed alone.
			var random = new Random(seed + s * 7919);
			var candles = new List<Candle>(barsPerSymbol);
			var price = 100m + s * 25m;
			var time = first;

			for (var i = 0; i < barsPerSymbol; i++)
			{
				var drift = (decimal)((random.NextDouble() - 0.5) * 0.6);
				var range = (decimal)(random.NextDouble() * 0.5 + 0.05);

				var open = price;
				var close = Math.Round(Math.Max(1m, open + drift), 2);
				var high = Math.Round(Math.Max(open, close) + range, 2);
				var low = Math.Round(Math.Max(0.5m, Math.Min(open, close) - range), 2);
				var volume = Math.Round((decimal)(random.NextDouble() * 9000 + 1000), 0);

				candles.Add(new(time, open, high, low, close, volume));

				price = close;
				time += frame;
			}

			bars.Add(symbols[s], candles);
		}

		return DatasetBuilder.Build(bars, frame, Source, isSynthetic: true);
	}
}
