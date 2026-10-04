namespace Odysseus.Platform;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Ecng.Common;

using StockSharp.Algo.Strategies;
using StockSharp.Messages;

/// <summary>
/// Builds a running strategy out of a compiled candidate.
/// </summary>
/// <remarks>
/// Visible only to the two projects that are allowed to load a candidate: the isolated worker, where a
/// backtest and a search run, and the broker adapter, where a finished candidate is put on a paper
/// account. A host has no member to call, which is a stronger statement than a rule about one.
///
/// The assembly is loaded into the default context and stays there. In the worker that is deliberate -
/// the process is recycled, which is what unloading is for - and on the paper path it is the one place
/// where a candidate is loaded in a long-lived process, because a deployment is one assembly against one
/// account for as long as it runs, against four hundred backtests in a session.
/// </remarks>
internal static class StrategyLoader
{
	/// <summary>
	/// Builds the strategy out of a compiled candidate.
	/// </summary>
	/// <param name="assembly">The compiled candidate.</param>
	/// <param name="className">Name of the strategy type inside it.</param>
	/// <param name="timeFrame">Length of one candle.</param>
	/// <param name="parameters">Values to set, or nothing for the ones the strategy declares.</param>
	/// <param name="entryDelayBars">Candles between an entry signal and the order it produces.</param>
	/// <returns>The strategy.</returns>
	public static Strategy Instantiate(
		byte[] assembly,
		string className,
		TimeSpan timeFrame,
		IReadOnlyDictionary<string, decimal> parameters,
		int entryDelayBars)
	{
		ArgumentNullException.ThrowIfNull(assembly);

		var types = Assembly.Load(assembly)
			.GetTypes()
			.Where(t => typeof(Strategy).IsAssignableFrom(t) && !t.IsAbstract)
			.ToArray();

		// By name when the caller has one. Two candidates can compile to assemblies whose strategy classes
		// share a simple name, because the name is derived from what the specification was called; picking
		// "the only one" would then pick whichever happened to be there.
		var type = (string.IsNullOrWhiteSpace(className)
			? types.SingleOrDefault()
			: types.FirstOrDefault(t => t.Name == className || t.FullName == className))
			?? throw new ArgumentException(
				string.IsNullOrWhiteSpace(className)
					? "The compiled candidate holds no strategy to run."
					: $"The compiled candidate holds no strategy called '{className}'. It holds: " +
						$"{string.Join(", ", types.Select(t => t.FullName))}.",
				nameof(assembly));

		var strategy = (Strategy)Activator.CreateInstance(type);

		// The candles the rules are evaluated on come from the dataset rather than from the default the
		// specification declared: a run over five-minute bars with a strategy asking for fifteen would
		// subscribe to something the engine has never been given and trade nothing at all.
		Set(strategy, "CandleType", timeFrame.TimeFrame());

		// Not one of the specification's parameters: the rules are the same whether the fill is instant or
		// a candle late, and this says which the run is measuring.
		if (entryDelayBars > 0)
			Set(strategy, "EntryDelayBars", entryDelayBars);

		foreach (var (name, value) in parameters ?? new Dictionary<string, decimal>())
			Set(strategy, name, value);

		return strategy;
	}

	private static void Set(Strategy strategy, string name, object value)
	{
		var property = strategy.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
			?? throw new ArgumentException(
				$"The strategy declares no parameter named '{name}'. It declares: " +
				$"{string.Join(", ", strategy.GetType().GetProperties().Select(p => p.Name))}.",
				nameof(name));

		property.SetValue(strategy, value.To(property.PropertyType));
	}
}
