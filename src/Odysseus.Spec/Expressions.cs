namespace Odysseus.Spec;

using System.Collections.Generic;

/// <summary>
/// A node of the expression tree a rule is written in.
/// </summary>
/// <remarks>
/// A rule is a tree rather than a piece of C# because the agent should be arguing about the strategy,
/// not about syntax. Everything expressible here can be translated to code mechanically, and nothing
/// else is expressible at all, so a specification cannot describe a strategy the translator would have
/// to guess at.
/// </remarks>
public abstract record Expression
{
	/// <summary>Children of this node, in evaluation order.</summary>
	public virtual IEnumerable<Expression> Children => [];
}

/// <summary>Every condition must hold.</summary>
/// <param name="Conditions">Conditions that must all hold.</param>
public sealed record AllOf(IReadOnlyList<Expression> Conditions) : Expression
{
	/// <inheritdoc />
	public override IEnumerable<Expression> Children => Conditions;
}

/// <summary>At least one condition must hold.</summary>
/// <param name="Conditions">Conditions of which one must hold.</param>
public sealed record AnyOf(IReadOnlyList<Expression> Conditions) : Expression
{
	/// <inheritdoc />
	public override IEnumerable<Expression> Children => Conditions;
}

/// <summary>The child must not hold.</summary>
/// <param name="Condition">Condition to negate.</param>
public sealed record Not(Expression Condition) : Expression
{
	/// <inheritdoc />
	public override IEnumerable<Expression> Children => [Condition];
}

/// <summary>How two values are compared.</summary>
public enum ComparisonOperators
{
	/// <summary>Strictly greater.</summary>
	GreaterThan,

	/// <summary>Greater or equal.</summary>
	GreaterOrEqual,

	/// <summary>Strictly less.</summary>
	LessThan,

	/// <summary>Less or equal.</summary>
	LessOrEqual,
}

/// <summary>Two values compared.</summary>
/// <param name="Left">Left side.</param>
/// <param name="Operator">How they are compared.</param>
/// <param name="Right">Right side.</param>
public sealed record Compare(Expression Left, ComparisonOperators Operator, Expression Right) : Expression
{
	/// <inheritdoc />
	public override IEnumerable<Expression> Children => [Left, Right];
}

/// <summary>Arithmetic performed on values.</summary>
public enum ArithmeticOperators
{
	/// <summary>Sum of the operands.</summary>
	Add,

	/// <summary>First operand minus the rest.</summary>
	Subtract,

	/// <summary>Product of the operands.</summary>
	Multiply,

	/// <summary>First operand divided by the rest.</summary>
	Divide,

	/// <summary>Smallest operand.</summary>
	Min,

	/// <summary>Largest operand.</summary>
	Max,
}

/// <summary>Arithmetic over several values.</summary>
/// <param name="Operator">Operation to perform.</param>
/// <param name="Operands">Values to combine.</param>
public sealed record Arithmetic(ArithmeticOperators Operator, IReadOnlyList<Expression> Operands) : Expression
{
	/// <inheritdoc />
	public override IEnumerable<Expression> Children => Operands;
}

/// <summary>Absolute value.</summary>
/// <param name="Value">Value to take the magnitude of.</param>
public sealed record AbsoluteValue(Expression Value) : Expression
{
	/// <inheritdoc />
	public override IEnumerable<Expression> Children => [Value];
}

/// <summary>Fields of a finished candle.</summary>
public enum CandleFields
{
	/// <summary>First traded price.</summary>
	Open,

	/// <summary>Highest traded price.</summary>
	High,

	/// <summary>Lowest traded price.</summary>
	Low,

	/// <summary>Last traded price.</summary>
	Close,

	/// <summary>Volume traded.</summary>
	Volume,
}

/// <summary>A field of a finished candle.</summary>
/// <param name="Which">Field to read.</param>
/// <param name="Offset">How many candles back, zero being the candle just finished.</param>
public sealed record Field(CandleFields Which, int Offset = 0) : Expression;

/// <summary>A declared parameter.</summary>
/// <param name="Name">Name of the parameter.</param>
public sealed record ParameterRef(string Name) : Expression;

/// <summary>A fixed number written into the rule.</summary>
/// <param name="Value">The number.</param>
public sealed record Constant(decimal Value) : Expression;

/// <summary>The current value of an indicator.</summary>
/// <param name="Name">Catalog name of the indicator.</param>
/// <param name="Length">Window length, as a declared parameter or a constant.</param>
/// <param name="Source">Candle field the indicator reads, when it accepts a choice.</param>
/// <param name="Offset">How many candles back, zero being the candle just finished.</param>
public sealed record IndicatorRef(string Name, Expression Length, CandleFields? Source = null, int Offset = 0) : Expression
{
	/// <inheritdoc />
	public override IEnumerable<Expression> Children => [Length];
}

/// <summary>Direction of a crossing.</summary>
public enum CrossDirections
{
	/// <summary>The left side moved from below the right side to above it.</summary>
	Above,

	/// <summary>The left side moved from above the right side to below it.</summary>
	Below,
}

/// <summary>One value crossing another between the previous candle and this one.</summary>
/// <param name="Left">Value that crosses.</param>
/// <param name="Direction">Which way it crossed.</param>
/// <param name="Right">Value that is crossed.</param>
public sealed record Cross(Expression Left, CrossDirections Direction, Expression Right) : Expression
{
	/// <inheritdoc />
	public override IEnumerable<Expression> Children => [Left, Right];
}

/// <summary>States a position can be in.</summary>
public enum PositionStates
{
	/// <summary>No position is open.</summary>
	Flat,

	/// <summary>A long position is open.</summary>
	Long,

	/// <summary>A short position is open.</summary>
	Short,
}

/// <summary>Whether the position is in a given state.</summary>
/// <param name="State">State to test for.</param>
public sealed record PositionIs(PositionStates State) : Expression;

/// <summary>Whether the candle falls inside a window of the trading session.</summary>
/// <param name="NotBefore">Earliest time of day, in the session time zone.</param>
/// <param name="NotAfter">Latest time of day, in the session time zone.</param>
public sealed record SessionWindow(string NotBefore, string NotAfter) : Expression;
