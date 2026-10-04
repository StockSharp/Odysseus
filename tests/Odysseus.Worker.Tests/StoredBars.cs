namespace Odysseus.Worker.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.Platform;

/// <summary>
/// Bars stored where a run can read them, the way an import stores them.
/// </summary>
/// <remarks>
/// A run is pointed at a folder rather than handed candles, so a test that drives one has to put its
/// made-up bars where an import would have put them. Through the same store the product writes with,
/// so what these tests measure is the format a real run reads and not a second one kept for tests.
/// </remarks>
internal static class StoredBars
{
	private static readonly string _root = Path.Combine(Path.GetTempPath(), "odysseus-tests", "worker-bars");

	/// <summary>
	/// Stores bars in a folder of their own.
	/// </summary>
	/// <param name="bars">The bars, oldest first.</param>
	/// <param name="symbol">Symbol they belong to.</param>
	/// <param name="timeFrame">Length of one candle.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Where they are and what they span.</returns>
	public static async Task<BarRange> WriteAsync(
		IReadOnlyList<Candle> bars,
		string symbol,
		TimeSpan timeFrame,
		CancellationToken cancellationToken)
	{
		var folder = Path.Combine(_root, Guid.NewGuid().ToString("n"));

		await new LocalBarStorage(folder).WriteAsync(symbol, timeFrame, bars, cancellationToken);

		return bars.Count == 0
			? new(folder, default, default, 0)
			: new(folder, bars[0].OpenTime, bars[^1].OpenTime + timeFrame, bars.Count);
	}

	/// <summary>Removes everything the tests of this assembly froze.</summary>
	public static void Clear()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}
}
