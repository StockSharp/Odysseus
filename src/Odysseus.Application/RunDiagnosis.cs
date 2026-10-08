namespace StockSharp.Odysseus.Application;

using System.Linq;

/// <summary>
/// Why a run measured nothing.
/// </summary>
/// <remarks>
/// A run with no trades reports no profit, no drawdown and no exposure, and those numbers are the same
/// whether the rules never triggered or the orders were too large for the bars to fill. The first is an
/// answer about the hypothesis; the second means the hypothesis was never tested. Reporting them
/// identically invites the agent to abandon an idea because of how it was sized.
/// </remarks>
public static class RunDiagnosis
{
	/// <summary>
	/// Explains an empty result, when there is something to explain.
	/// </summary>
	/// <param name="ordersPlaced">Orders the strategy sent.</param>
	/// <param name="trades">Positions that were opened and closed.</param>
	/// <param name="volume">Size of one position.</param>
	/// <param name="bars">The bars the run saw.</param>
	/// <returns>What happened, or null when the run needs no explaining.</returns>
	public static string Explain(int ordersPlaced, int trades, decimal volume, IReadOnlyList<Candle> bars)
	{
		ArgumentNullException.ThrowIfNull(bars);

		if (trades > 0 || bars.Count == 0)
			return null;

		if (ordersPlaced == 0)
		{
			return
				"The rules never triggered on this slice: no order was placed at all. That is an answer " +
				"about the hypothesis rather than a failure — the condition as written did not occur here. " +
				"Check the warm-up is not eating the slice, then loosen the condition or test it where the " +
				"market does what it describes.";
		}

		var volumes = bars.Select(b => b.Volume).OrderBy(v => v).ToArray();
		var median = volumes[volumes.Length / 2];
		var tooThin = volumes.Count(v => v < volume) * 100m / volumes.Length;

		var placed = ordersPlaced == 1
			? "The strategy placed one order and it never filled"
			: $"The strategy placed {ordersPlaced} orders and not one of them filled";

		return
			$"{placed}, so this run measured nothing about the hypothesis. The order was for " +
			$"{volume:0.###} units, where the median bar traded {median:0.###} and {tooThin:0.#}% of bars " +
			"traded less than that. An order larger than the bar it lands on does not fill at all here — " +
			"not partly, not later — and while one stands unfilled the rules place no further orders, " +
			"which is why so few appear. Size the position to what the instrument actually trades, or " +
			"research one that trades more.";
	}
}
