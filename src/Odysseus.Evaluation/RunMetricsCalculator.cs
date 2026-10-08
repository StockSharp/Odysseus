namespace StockSharp.Odysseus.Evaluation;

using System;
using System.Collections.Generic;
using System.Linq;

using StockSharp.Odysseus.Domain;

/// <summary>
/// Turns what a run did into the numbers a verdict is read from.
/// </summary>
/// <remarks>
/// Nothing here knows how the run was produced. It is given the trades that closed and the equity curve
/// they moved, and it measures them — which is what lets the same measurement be applied to a backtest,
/// to a paper account and to a live one, and lets the measurement be checked without a market.
///
/// Where a ratio has no meaning the answer is absence rather than a stand-in. A profit factor over no
/// losing trades and a recovery factor over no drawdown are both undefined, and reporting zero for
/// either would read as the worst possible result for what is in fact the best.
/// </remarks>
public static class RunMetricsCalculator
{
	/// <summary>
	/// Measures a run.
	/// </summary>
	/// <param name="trades">Positions that were opened and closed, in the order they closed.</param>
	/// <param name="equity">Account value sampled through the run.</param>
	/// <param name="startingEquity">Money the slice started with, which the return is measured against.</param>
	/// <param name="tradableTime">How long the slice was tradable, which exposure is measured against.</param>
	/// <param name="executionErrorCount">Orders the run could not place or fill.</param>
	/// <returns>The measured run.</returns>
	public static RunMetrics Measure(
		IReadOnlyList<ExecutedTrade> trades,
		IReadOnlyList<EquityPoint> equity,
		decimal startingEquity,
		TimeSpan tradableTime,
		int executionErrorCount = 0)
	{
		ArgumentNullException.ThrowIfNull(trades);
		ArgumentNullException.ThrowIfNull(equity);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startingEquity);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tradableTime.Ticks);
		ArgumentOutOfRangeException.ThrowIfNegative(executionErrorCount);

		var commission = trades.Sum(t => t.Commission);
		var slippage = trades.Sum(t => t.Slippage);
		var net = trades.Sum(t => t.Net);
		var drawdown = Drawdown(equity);

		return new(
			Profit(trades, t => t.Gross, startingEquity),
			Profit(trades, t => t.Net, startingEquity),
			new(commission, slippage),
			new(
				drawdown.Percent,
				drawdown.Amount,
				drawdown.Amount == 0 ? null : net / drawdown.Amount),
			new(
				trades.Count,
				Share(trades.Count(t => t.Net > 0), trades.Count),
				trades.Count == 0 ? 0 : trades.Sum(t => (decimal)t.Holding.TotalMinutes) / trades.Count),
			new(
				trades.Sum(t => t.Turnover),
				Share(trades.Sum(t => t.Holding.Ticks), tradableTime.Ticks)),
			Concentration(trades, net),
			executionErrorCount);
	}

	private static ProfitBlock Profit(
		IReadOnlyList<ExecutedTrade> trades,
		Func<ExecutedTrade, decimal> result,
		decimal startingEquity)
	{
		var total = trades.Sum(result);
		var losses = trades.Where(t => result(t) < 0).Sum(t => -result(t));

		return new(
			total,
			total / startingEquity * 100m,
			losses == 0 ? null : trades.Where(t => result(t) > 0).Sum(result) / losses,
			trades.Count == 0 ? 0 : total / trades.Count);
	}

	private static (decimal Amount, decimal Percent) Drawdown(IReadOnlyList<EquityPoint> equity)
	{
		var peak = 0m;
		var amount = 0m;
		var percent = 0m;

		foreach (var point in equity)
		{
			if (point.Equity > peak)
				peak = point.Equity;

			var fall = peak - point.Equity;

			// The deepest fall in money and the deepest fall in percent can be different falls: a small
			// account losing half is a deeper fall than a grown one losing a tenth of a larger sum.
			if (fall > amount)
				amount = fall;

			if (peak > 0 && fall / peak * 100m > percent)
				percent = fall / peak * 100m;
		}

		return (amount, percent);
	}

	private static ConcentrationBlock Concentration(IReadOnlyList<ExecutedTrade> trades, decimal net)
	{
		var best = trades.OrderByDescending(t => t.Net).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();

		var bestSymbol = trades
			.GroupBy(t => t.Symbol, StringComparer.Ordinal)
			.Select(g => (Symbol: g.Key, Net: g.Sum(t => t.Net)))
			.OrderByDescending(g => g.Net)
			.ThenBy(g => g.Symbol, StringComparer.Ordinal)
			.FirstOrDefault();

		// A share of a loss is not a share of anything. Dividing by a negative total would turn the
		// trade that lost least into the one that carried the result.
		if (net <= 0 || best is null)
			return new(0, 0, best?.Id ?? string.Empty, bestSymbol.Symbol ?? string.Empty);

		return new(
			Share(best.Net, net),
			Share(bestSymbol.Net, net),
			best.Id,
			bestSymbol.Symbol);
	}

	private static decimal Share(decimal part, decimal whole)
		=> whole == 0 ? 0 : part / whole * 100m;
}
