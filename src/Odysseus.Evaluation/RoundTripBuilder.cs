namespace StockSharp.Odysseus.Evaluation;

using System;
using System.Collections.Generic;
using System.Globalization;

using StockSharp.Odysseus.Domain;

/// <summary>
/// Joins the fills of a run into the trades it made.
/// </summary>
/// <remarks>
/// An engine reports fills. It says a hundred shares were bought, and later that a hundred were sold,
/// and nothing in either statement says the second closed the first. Everything a result is read from —
/// what a trade made, how long it was held, whether one trade carried the whole run — lives in that
/// join, so it is made here once and read everywhere else.
///
/// A trade is one stretch from flat to flat. Building a position in pieces and letting it go in pieces
/// is still one trade, priced at what the pieces averaged. Counting each piece separately would make
/// the trade count something a strategy could raise by scaling in and out, and the trade count is how
/// much evidence a result rests on: thirty trades assembled out of six positions is six pieces of
/// evidence, not thirty.
/// </remarks>
public static class RoundTripBuilder
{
	/// <summary>
	/// Builds the trades of a run.
	/// </summary>
	/// <param name="fills">Fills in the order they happened.</param>
	/// <returns>The closed trades, in the order they closed.</returns>
	/// <exception cref="ArgumentException">The fills are not in time order.</exception>
	public static IReadOnlyList<ExecutedTrade> Build(IReadOnlyList<Fill> fills)
	{
		ArgumentNullException.ThrowIfNull(fills);

		var open = new Dictionary<string, Episode>(StringComparer.Ordinal);
		var closed = new List<ExecutedTrade>();
		var previous = DateTime.MinValue;

		foreach (var fill in fills)
		{
			if (fill.Time < previous)
			{
				throw new ArgumentException(
					$"A fill at {fill.Time:O} follows one at {previous:O}. Fills are joined into trades by the " +
					"order they happened, and out of order they would produce trades that closed before they opened.",
					nameof(fills));
			}

			previous = fill.Time;

			Apply(open, fill, closed);
		}

		return closed;
	}

	private static void Apply(Dictionary<string, Episode> open, Fill fill, List<ExecutedTrade> closed)
	{
		var remaining = fill.Volume;

		while (remaining > 0)
		{
			if (!open.TryGetValue(fill.Symbol, out var episode))
			{
				open[fill.Symbol] = new(fill.Side, fill.Time);
				continue;
			}

			if (episode.Side == fill.Side)
			{
				episode.Enter(fill, remaining);

				return;
			}

			var closing = Math.Min(remaining, episode.Outstanding);

			episode.Exit(fill, closing);

			remaining -= closing;

			if (!episode.IsFlat)
				return;

			closed.Add(episode.ToTrade(closed.Count + 1));
			open.Remove(fill.Symbol);
		}
	}

	/// <summary>
	/// One stretch from flat to flat, while it is still being built.
	/// </summary>
	private sealed class Episode(TradeDirections side, DateTime openedAt)
	{
		private decimal _enteredVolume;
		private decimal _enteredNotional;
		private decimal _exitedVolume;
		private decimal _exitedNotional;
		private decimal _commission;
		private decimal _slippage;
		private DateTime _closedAt;

		public TradeDirections Side => side;

		/// <summary>How much of the position is still open.</summary>
		public decimal Outstanding => _enteredVolume - _exitedVolume;

		/// <summary>Whether the position has come back to flat, which ends the trade.</summary>
		public bool IsFlat => _enteredVolume > 0 && Outstanding == 0;

		public string Symbol { get; private set; }

		public void Enter(Fill fill, decimal volume)
		{
			_enteredVolume += volume;
			_enteredNotional += fill.Price * volume;

			Charge(fill, volume);
		}

		public void Exit(Fill fill, decimal volume)
		{
			_exitedVolume += volume;
			_exitedNotional += fill.Price * volume;
			_closedAt = fill.Time;

			Charge(fill, volume);
		}

		public ExecutedTrade ToTrade(int number)
			=> new(
				// Numbered rather than random, so the same run names the same trade every time it is measured.
				$"trade-{number.ToString("D4", CultureInfo.InvariantCulture)}",
				Symbol,
				side,
				openedAt,
				_enteredNotional / _enteredVolume,
				_closedAt,
				_exitedNotional / _exitedVolume,
				_enteredVolume,
				_commission,
				_slippage);

		// Costs are charged to the whole fill, so a fill split between closing one position and opening
		// the next carries the part of them it used.
		private void Charge(Fill fill, decimal volume)
		{
			var share = volume / fill.Volume;

			_commission += fill.Commission * share;
			_slippage += fill.Slippage * share;

			Symbol = fill.Symbol;
		}
	}
}
