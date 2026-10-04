namespace Odysseus.Spec;

using System.Text.Json.Serialization;

using Odysseus.Domain;

/// <summary>Types a declared parameter can take.</summary>
public enum ParameterTypes
{
	/// <summary>A whole number, which is what every window length is.</summary>
	Integer,

	/// <summary>A fixed-point number, used by multipliers and thresholds.</summary>
	Decimal,
}

/// <summary>
/// A number the optimizer is allowed to search.
/// </summary>
/// <param name="Name">Name the rules refer to.</param>
/// <param name="Type">Type of the value.</param>
/// <param name="Default">Value used before any search.</param>
/// <param name="Minimum">Smallest value the search may try.</param>
/// <param name="Maximum">Largest value the search may try.</param>
/// <param name="Step">Distance between values the search tries.</param>
/// <param name="Optimizable">Whether the search may vary it at all.</param>
/// <remarks>
/// Declaring the range here rather than writing a number into a rule is what keeps the division of
/// labour honest: the agent decides what a strategy is, the optimizer decides what its numbers are.
/// A constant buried in a rule is a number nobody searched and nobody can report on.
/// </remarks>
public sealed record ParameterDefinition(
	string Name,
	ParameterTypes Type,
	decimal Default,
	decimal Minimum,
	decimal Maximum,
	decimal Step,
	bool Optimizable = true);

/// <summary>A condition that opens a position.</summary>
/// <param name="Id">Identifier used by reports and diagnostics.</param>
/// <param name="Direction">Direction the rule opens in.</param>
/// <param name="Condition">Condition that must hold.</param>
public sealed record EntryRule(string Id, TradeDirections Direction, Expression Condition);

/// <summary>Ways a position can be closed.</summary>
public enum ExitKinds
{
	/// <summary>A condition written as an expression.</summary>
	Condition,

	/// <summary>A stop placed a multiple of the average true range away.</summary>
	AtrStop,

	/// <summary>A target placed a multiple of the average true range away.</summary>
	AtrTarget,

	/// <summary>Closing after a fixed number of candles.</summary>
	TimeExit,

	/// <summary>Closing before the session ends.</summary>
	SessionEnd,
}

/// <summary>A condition that closes a position.</summary>
/// <param name="Id">Identifier used by reports and diagnostics.</param>
/// <param name="Kind">How the exit works.</param>
/// <param name="Direction">Direction the exit applies to.</param>
/// <param name="Condition">Condition, when the exit is written as one.</param>
/// <param name="Length">Window length or candle count, when the exit needs one.</param>
/// <param name="Multiplier">Multiple of the range, when the exit needs one.</param>
public sealed record ExitRule(
	string Id,
	ExitKinds Kind,
	TradeDirections Direction,
	Expression Condition = null,
	Expression Length = null,
	Expression Multiplier = null);

/// <summary>Limits that hold regardless of what the rules say.</summary>
/// <param name="MaxPositionPercent">
/// Largest share of the account one position may take, as a fraction. This is what sizes a position:
/// the run buys that much of the instrument at the first price of the slice and holds the size for the
/// whole run.
/// </param>
/// <param name="MaxDailyLossPercent">
/// Loss in one market day, as a fraction of the account, after which the strategy closes what it holds
/// and stops opening anything until the next day. Zero for no limit.
/// </param>
public sealed record RiskLimits(decimal MaxPositionPercent, decimal MaxDailyLossPercent);

/// <summary>
/// A strategy, described completely enough to be translated into code without anything being guessed.
/// </summary>
public sealed record StrategySpec
{
	/// <summary>Name a person would use for the strategy.</summary>
	public required string Name { get; init; }

	/// <summary>Why the strategy is expected to work, in one or two sentences.</summary>
	public required string Thesis { get; init; }

	/// <summary>Whether long positions are allowed.</summary>
	public required bool AllowLong { get; init; }

	/// <summary>Whether short positions are allowed.</summary>
	public required bool AllowShort { get; init; }

	/// <summary>
	/// Candles the rules are evaluated on.
	/// </summary>
	/// <remarks>
	/// Part of the hypothesis rather than of the data: an average of twenty five-minute bars and an
	/// average of twenty daily ones are different claims about the market. A run over bars of another
	/// length is refused rather than quietly measured, because it would measure a strategy nobody wrote.
	/// </remarks>
	public required TimeSpan TimeFrame { get; init; }

	/// <summary>
	/// Candles that must close before any rule may act. The first rule is read off the last of them, and
	/// the order it sends fills on the candle after that.
	/// </summary>
	public required int WarmupBars { get; init; }

	/// <summary>Conditions that open a position.</summary>
	public required IReadOnlyList<EntryRule> Entries { get; init; }

	/// <summary>Conditions that close a position.</summary>
	public required IReadOnlyList<ExitRule> Exits { get; init; }

	/// <summary>Numbers the optimizer may search.</summary>
	public required IReadOnlyList<ParameterDefinition> Parameters { get; init; }

	/// <summary>Limits that hold regardless of the rules.</summary>
	public required RiskLimits Risk { get; init; }

	/// <summary>What the agent assumed while writing this down.</summary>
	public IReadOnlyList<string> Assumptions { get; init; } = [];

	/// <summary>What would show the thesis to be wrong.</summary>
	public IReadOnlyList<string> InvalidationConditions { get; init; } = [];

	/// <summary>Every expression in the specification, entries and exits alike.</summary>
	/// <remarks>
	/// Derived, so it is not written out: a specification that carried it would invite a reader to send
	/// it back, and the server would ignore what it sent. What is on the wire is what can be read.
	/// </remarks>
	[JsonIgnore]
	public IEnumerable<Expression> AllExpressions
	{
		get
		{
			foreach (var entry in Entries)
				yield return entry.Condition;

			foreach (var exit in Exits)
			{
				if (exit.Condition is not null)
					yield return exit.Condition;

				if (exit.Length is not null)
					yield return exit.Length;

				if (exit.Multiplier is not null)
					yield return exit.Multiplier;
			}
		}
	}
}
