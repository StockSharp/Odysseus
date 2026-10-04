namespace Odysseus.Spec;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>How an indicator is fed.</summary>
public enum IndicatorSources
{
	/// <summary>The specification chooses which candle field the indicator reads.</summary>
	ChosenField,

	/// <summary>The indicator always reads one particular field and the specification cannot change it.</summary>
	FixedField,

	/// <summary>The indicator needs the whole candle and offers no choice.</summary>
	WholeCandle,
}

/// <summary>
/// One indicator a specification may refer to.
/// </summary>
/// <param name="Name">Name a specification writes.</param>
/// <param name="Title">Name a person reads.</param>
/// <param name="Description">What it computes.</param>
/// <param name="Source">How it is fed.</param>
/// <param name="Field">Field it reads, when that is fixed or is the default.</param>
/// <param name="MinimumLength">Shortest window it can be computed over.</param>
/// <param name="MaximumLength">Longest window the catalog allows.</param>
public sealed record IndicatorDefinition(
	string Name,
	string Title,
	string Description,
	IndicatorSources Source,
	CandleFields? Field,
	int MinimumLength,
	int MaximumLength);

/// <summary>
/// The indicators a specification may use.
/// </summary>
/// <remarks>
/// This is the single source of truth, and a specification naming anything absent from it is refused
/// before a line of code is generated. The alternative — letting a name through and discovering at
/// compile time that nothing implements it — turns a clear refusal into a compiler error the agent has
/// to reverse-engineer.
/// </remarks>
public static class IndicatorCatalog
{
	/// <summary>Longest window any indicator may be asked for.</summary>
	public const int MaximumLength = 500;

	private static readonly IReadOnlyList<IndicatorDefinition> _all =
	[
		new("sma", "Simple Moving Average", "Mean of a candle field over the window.",
			IndicatorSources.ChosenField, CandleFields.Close, 2, MaximumLength),

		new("ema", "Exponential Moving Average", "Weighted mean of a candle field, favouring recent candles.",
			IndicatorSources.ChosenField, CandleFields.Close, 2, MaximumLength),

		new("rsi", "Relative Strength Index", "Ratio of average gains to average losses over the window, from 0 to 100.",
			IndicatorSources.ChosenField, CandleFields.Close, 2, MaximumLength),

		new("highest", "Highest Value", "Largest value of a candle field over the window.",
			IndicatorSources.ChosenField, CandleFields.High, 2, MaximumLength),

		new("lowest", "Lowest Value", "Smallest value of a candle field over the window.",
			IndicatorSources.ChosenField, CandleFields.Low, 2, MaximumLength),

		new("stdDev", "Standard Deviation", "Dispersion of a candle field around its mean over the window.",
			IndicatorSources.ChosenField, CandleFields.Close, 2, MaximumLength),

		new("atr", "Average True Range", "Mean true range over the window; needs the whole candle and the one before it.",
			IndicatorSources.WholeCandle, null, 2, MaximumLength),

		new("volumeSma", "Volume Moving Average", "Mean traded volume over the window.",
			IndicatorSources.FixedField, CandleFields.Volume, 2, MaximumLength),
	];

	/// <summary>Every indicator a specification may use.</summary>
	public static IReadOnlyList<IndicatorDefinition> All => _all;

	/// <summary>
	/// Looks an indicator up.
	/// </summary>
	/// <param name="name">Name a specification wrote.</param>
	/// <returns>The definition, or <see langword="null"/> when the catalog has no such indicator.</returns>
	public static IndicatorDefinition Find(string name)
		=> _all.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));

	/// <summary>
	/// Names close enough to what was written to be worth suggesting.
	/// </summary>
	/// <param name="name">Name a specification wrote.</param>
	/// <param name="count">How many suggestions to return.</param>
	/// <returns>The nearest names, closest first.</returns>
	/// <remarks>
	/// A refusal that only says the name is unknown leaves the caller guessing among eight
	/// possibilities. Naming the nearest ones usually turns the next attempt into the right one.
	/// </remarks>
	public static IReadOnlyList<string> Nearest(string name, int count = 3)
	{
		if (string.IsNullOrWhiteSpace(name))
			return [.. _all.Take(count).Select(i => i.Name)];

		return
		[
			.. _all
				.OrderBy(i => Distance(i.Name.ToLowerInvariant(), name.ToLowerInvariant()))
				.Take(count)
				.Select(i => i.Name),
		];
	}

	private static int Distance(string left, string right)
	{
		var previous = new int[right.Length + 1];
		var current = new int[right.Length + 1];

		for (var j = 0; j <= right.Length; j++)
			previous[j] = j;

		for (var i = 1; i <= left.Length; i++)
		{
			current[0] = i;

			for (var j = 1; j <= right.Length; j++)
			{
				var substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);

				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
			}

			(previous, current) = (current, previous);
		}

		return previous[right.Length];
	}
}
