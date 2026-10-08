namespace StockSharp.Odysseus.Spec;

using StockSharp.Odysseus.Domain;

/// <summary>
/// One thing wrong with a specification.
/// </summary>
/// <param name="Path">Where in the specification it is, as a path a reader can follow.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Remedy">What would fix it.</param>
/// <remarks>
/// The path is not decoration. A refusal that names no location leaves the caller comparing its whole
/// document against a sentence, and the attempt it spends doing that is an attempt it does not spend
/// improving the strategy.
/// </remarks>
public sealed record SpecProblem(string Path, string Message, string Remedy);

/// <summary>
/// The outcome of checking a specification.
/// </summary>
/// <param name="Problems">Everything found wrong, in the order it was found.</param>
public sealed record SpecValidation(IReadOnlyList<SpecProblem> Problems)
{
	/// <summary>Whether the specification may be turned into code.</summary>
	public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Checks that a specification says enough, and nothing contradictory, to be turned into code.
/// </summary>
/// <remarks>
/// Every check here exists because the alternative is worse further down: a missing parameter becomes a
/// compiler error, an unknown indicator becomes a runtime failure, too little warm-up becomes a
/// backtest whose first trades were taken on indicators that had not formed yet — and that last one
/// does not fail at all, it just quietly reports a result that is not real.
/// </remarks>
public static class SpecValidator
{
	/// <summary>Most rules a specification may contain.</summary>
	public const int MaxRules = 12;

	/// <summary>Most distinct indicators a specification may refer to.</summary>
	public const int MaxIndicators = 12;

	/// <summary>Most parameters the optimizer may be asked to search.</summary>
	public const int MaxOptimizableParameters = 8;

	/// <summary>
	/// Longest window an indicator may look back over, and therefore the largest warm-up a run may need.
	/// </summary>
	/// <remarks>
	/// A ceiling rather than a judgement about what is sensible. An indicator length is the size of a
	/// buffer the engine allocates, and nothing else bounded it: a length of fifty million passed every
	/// check here and asked the run for as much memory as the machine had. Fifty thousand candles is
	/// about a year of one-minute bars, which is longer than any window a hypothesis over ninety days of
	/// history can mean.
	/// </remarks>
	public const int MaxLookbackBars = 50_000;

	/// <summary>
	/// Checks a specification.
	/// </summary>
	/// <param name="spec">Specification to check.</param>
	/// <returns>Everything found wrong.</returns>
	public static SpecValidation Validate(StrategySpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);

		var problems = new List<SpecProblem>();

		CheckShape(spec, problems);
		CheckParameters(spec, problems);
		CheckDirections(spec, problems);
		CheckExpressions(spec, problems);
		CheckWarmup(spec, problems);
		CheckComplexity(spec, problems);

		return new(problems);
	}

	/// <summary>
	/// How many candles must pass before every rule can be evaluated.
	/// </summary>
	/// <param name="spec">Specification to measure.</param>
	/// <returns>The number of candles.</returns>
	public static int RequiredWarmup(StrategySpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);

		var longest = 0;

		foreach (var expression in spec.AllExpressions)
			longest = Math.Max(longest, Lookback(expression, spec));

		return longest;
	}

	private static void CheckShape(StrategySpec spec, List<SpecProblem> problems)
	{
		if (string.IsNullOrWhiteSpace(spec.Name))
			problems.Add(new("name", "The strategy has no name.", "Give it a short name a person would use."));

		if (string.IsNullOrWhiteSpace(spec.Thesis))
		{
			problems.Add(new("thesis",
				"The strategy has no thesis.",
				"State in one or two sentences why this is expected to work. A candidate without one cannot be argued with when it fails."));
		}

		if (spec.Entries.Count == 0)
			problems.Add(new("entries", "The strategy never opens a position.", "Add at least one entry rule."));

		if (spec.Exits.Count == 0)
		{
			problems.Add(new("exits",
				"The strategy never closes a position.",
				"Add at least one exit rule. Without one a position is held to the end of the data, and the result measures the market rather than the strategy."));
		}

		if (!spec.AllowLong && !spec.AllowShort)
			problems.Add(new("allowLong", "Neither direction is allowed.", "Allow long, short, or both."));

		if (spec.TimeFrame <= TimeSpan.Zero)
			problems.Add(new("timeFrame", "The candle length is not positive.", "Use the timeframe the dataset was imported at."));

		if (spec.Risk.MaxPositionPercent is <= 0 or > 1)
		{
			problems.Add(new("risk.maxPositionPercent",
				$"A position may take {spec.Risk.MaxPositionPercent} of the account, which is outside the range 0 to 1.",
				"Express it as a fraction, for example 0.1 for a tenth of the account."));
		}

		if (spec.Risk.MaxDailyLossPercent is < 0 or > 1)
		{
			problems.Add(new("risk.maxDailyLossPercent",
				$"The daily loss limit is {spec.Risk.MaxDailyLossPercent}, which is outside the range 0 to 1.",
				"Express it as a fraction of the account, for example 0.02 for two percent, or 0 for no limit."));
		}
	}

	private static void CheckParameters(StrategySpec spec, List<SpecProblem> problems)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var parameter in spec.Parameters)
		{
			var path = $"parameters.{parameter.Name}";

			if (!seen.Add(parameter.Name))
				problems.Add(new(path, "Two parameters share this name.", "Give each parameter its own name."));

			if (parameter.Minimum > parameter.Maximum)
			{
				problems.Add(new(path,
					$"The range runs backwards: minimum {parameter.Minimum} is above maximum {parameter.Maximum}.",
					"Swap them."));
			}

			if (parameter.Default < parameter.Minimum || parameter.Default > parameter.Maximum)
			{
				problems.Add(new(path,
					$"The default {parameter.Default} lies outside the range {parameter.Minimum} to {parameter.Maximum}.",
					"Move the default inside the range, or widen the range to include it."));
			}

			if (parameter.Step <= 0)
				problems.Add(new(path, $"The step {parameter.Step} is not positive.", "Use a step the search can advance by."));

			if (parameter.Type == ParameterTypes.Integer && parameter.Step != Math.Truncate(parameter.Step))
			{
				problems.Add(new(path,
					$"A whole-number parameter cannot advance in steps of {parameter.Step}.",
					"Use a whole-number step, or declare the parameter as a decimal."));
			}
		}

		var optimizable = spec.Parameters.Count(p => p.Optimizable);

		if (optimizable > MaxOptimizableParameters)
		{
			problems.Add(new("parameters",
				$"{optimizable} parameters are marked for search; the limit is {MaxOptimizableParameters}.",
				"Fix the ones the thesis does not depend on. Every extra dimension multiplies the ways a result can be a coincidence."));
		}
	}

	private static void CheckDirections(StrategySpec spec, List<SpecProblem> problems)
	{
		foreach (var entry in spec.Entries)
		{
			var path = $"entries.{entry.Id}";

			if (entry.Direction == TradeDirections.Long && !spec.AllowLong)
				problems.Add(new(path, "This rule opens long, but long positions are not allowed.", "Allow long, or change the rule."));

			if (entry.Direction == TradeDirections.Short && !spec.AllowShort)
				problems.Add(new(path, "This rule opens short, but short positions are not allowed.", "Allow short, or change the rule."));

			if (!spec.Exits.Any(e => e.Direction == entry.Direction))
			{
				problems.Add(new(path,
					$"Nothing closes a {entry.Direction.ToString().ToLowerInvariant()} position, so one opened here is never closed.",
					$"Add an exit rule with direction {entry.Direction}."));
			}
		}

		var ids = new HashSet<string>(StringComparer.Ordinal);

		foreach (var id in spec.Entries.Select(e => e.Id).Concat(spec.Exits.Select(e => e.Id)))
		{
			if (!ids.Add(id))
				problems.Add(new($"rules.{id}", "Two rules share this identifier.", "Give each rule its own identifier; reports refer to them by it."));
		}
	}

	private static void CheckExpressions(StrategySpec spec, List<SpecProblem> problems)
	{
		var declared = spec.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

		foreach (var entry in spec.Entries)
			Walk(entry.Condition, $"entries.{entry.Id}.condition", declared, problems);

		foreach (var exit in spec.Exits)
		{
			if (exit.Condition is not null)
				Walk(exit.Condition, $"exits.{exit.Id}.condition", declared, problems);

			if (exit.Length is not null)
				Walk(exit.Length, $"exits.{exit.Id}.length", declared, problems);

			if (exit.Multiplier is not null)
				Walk(exit.Multiplier, $"exits.{exit.Id}.multiplier", declared, problems);

			if (exit.Kind is ExitKinds.AtrStop or ExitKinds.AtrTarget)
			{
				if (exit.Length is null)
					problems.Add(new($"exits.{exit.Id}.length", "An average-range exit needs a window length.", "Add a length, as a parameter or a number."));

				if (exit.Multiplier is null)
					problems.Add(new($"exits.{exit.Id}.multiplier", "An average-range exit needs a multiple of the range.", "Add a multiplier, as a parameter or a number."));
			}

			if (exit.Kind == ExitKinds.TimeExit && exit.Length is null)
				problems.Add(new($"exits.{exit.Id}.length", "A time exit needs a number of candles.", "Add a length, as a parameter or a number."));

			if (exit.Kind == ExitKinds.Condition && exit.Condition is null)
				problems.Add(new($"exits.{exit.Id}.condition", "A conditional exit needs a condition.", "Add the condition, or choose another kind of exit."));
		}
	}

	private static void Walk(Expression expression, string path, HashSet<string> declared, List<SpecProblem> problems)
	{
		switch (expression)
		{
			case ParameterRef reference when !declared.Contains(reference.Name):
			{
				var known = declared.Count == 0 ? "none are declared" : string.Join(", ", declared.Order(StringComparer.Ordinal));

				problems.Add(new(path,
					$"The rule refers to parameter '{reference.Name}', which is not declared.",
					$"Declare it, or use one that exists: {known}."));

				break;
			}

			case IndicatorRef indicator:
			{
				var definition = IndicatorCatalog.Find(indicator.Name);

				if (definition is null)
				{
					problems.Add(new(path,
						$"'{indicator.Name}' is not an indicator this server can compute.",
						$"Use one of these instead: {string.Join(", ", IndicatorCatalog.Nearest(indicator.Name))}. " +
						"The full list comes from get_indicator_catalog."));

					break;
				}

				if (indicator.Source is not null && definition.Source != IndicatorSources.ChosenField)
				{
					problems.Add(new(path,
						$"'{indicator.Name}' does not let the specification choose what it reads; it reads " +
						(definition.Source == IndicatorSources.WholeCandle ? "the whole candle." : $"{definition.Field}."),
						"Remove the source from this reference."));
				}

				break;
			}

			case Arithmetic arithmetic when arithmetic.Operator == ArithmeticOperators.Divide:
			{
				// A literal zero divisor is a crash waiting for the first candle; a parameter whose range
				// spans zero is a crash waiting for the first search that lands on it.
				foreach (var divisor in arithmetic.Operands.Skip(1))
				{
					if (divisor is Constant { Value: 0 })
						problems.Add(new(path, "The rule divides by zero.", "Divide by something that cannot be zero."));
				}

				break;
			}

			case Field field when field.Offset < 0:
				problems.Add(new(path, $"The rule reads {field.Offset} candles back.", "Offsets count backwards from the finished candle, so they are zero or more."));
				break;
		}

		foreach (var child in expression.Children)
			Walk(child, path, declared, problems);
	}

	private static void CheckWarmup(StrategySpec spec, List<SpecProblem> problems)
	{
		var required = RequiredWarmup(spec);

		if (required > MaxLookbackBars)
		{
			problems.Add(new("entries",
				$"The rules look back {required} candles, and this server runs nothing that looks back more than {MaxLookbackBars}.",
				"An indicator's length is the size of a buffer the run allocates, so a length nobody meant is a " +
				"run that ends on a memory limit rather than in a result. Shorten the longest window, or the " +
				"largest value the search may try for it."));
		}

		if (spec.WarmupBars > MaxLookbackBars)
		{
			problems.Add(new("warmupBars",
				$"Warm-up is {spec.WarmupBars} candles, and this server runs nothing that warms up for more than {MaxLookbackBars}.",
				$"Lower it to at most {MaxLookbackBars}. Warm-up is candles the run spends before it may act, so a " +
				"warm-up longer than the history is a run that never trades."));
		}

		if (spec.WarmupBars < required)
		{
			problems.Add(new("warmupBars",
				$"Warm-up is {spec.WarmupBars} candles, but the rules look back {required}.",
				$"Raise it to at least {required}. Trading before an indicator has formed does not fail; it quietly produces a result that is not real."));
		}
	}

	private static int Lookback(Expression expression, StrategySpec spec)
	{
		var own = expression switch
		{
			IndicatorRef indicator => LengthOf(indicator.Length, spec) + indicator.Offset,
			Field field => field.Offset + 1,
			// A crossing compares this candle with the previous one, so it always needs one more.
			Cross => 1,
			_ => 0,
		};

		foreach (var child in expression.Children)
			own = Math.Max(own, Lookback(child, spec));

		return own;
	}

	private static int LengthOf(Expression length, StrategySpec spec)
		=> length switch
		{
			Constant constant => (int)constant.Value,
			// The search may pick anything in the range, so warm-up has to cover the longest of them.
			ParameterRef reference => (int)(spec.Parameters
				.FirstOrDefault(p => string.Equals(p.Name, reference.Name, StringComparison.Ordinal))?.Maximum ?? 0),
			_ => 0,
		};

	private static void CheckComplexity(StrategySpec spec, List<SpecProblem> problems)
	{
		var rules = spec.Entries.Count + spec.Exits.Count;

		if (rules > MaxRules)
		{
			problems.Add(new("rules",
				$"The strategy has {rules} rules; the limit is {MaxRules}.",
				"Remove the ones the thesis does not need. A rule that only helps on the data it was written for is how a result stops meaning anything."));
		}

		var indicators = spec.AllExpressions
			.SelectMany(Flatten)
			.OfType<IndicatorRef>()
			.Select(i => $"{i.Name}:{i.Length}:{i.Source}")
			.Distinct(StringComparer.Ordinal)
			.Count();

		if (indicators > MaxIndicators)
		{
			problems.Add(new("indicators",
				$"The strategy uses {indicators} distinct indicators; the limit is {MaxIndicators}.",
				"Drop the ones that measure the same thing twice."));
		}
	}

	private static IEnumerable<Expression> Flatten(Expression expression)
	{
		yield return expression;

		foreach (var child in expression.Children)
		{
			foreach (var nested in Flatten(child))
				yield return nested;
		}
	}
}
