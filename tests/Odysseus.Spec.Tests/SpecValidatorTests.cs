namespace Odysseus.Spec.Tests;

/// <summary>
/// Checking a specification before anything is generated from it.
/// </summary>
/// <remarks>
/// The assertions are mostly about what a refusal says, not that it happened. A refusal is the next
/// prompt: it has to name the place, say what is wrong, and say what would fix it, or the caller spends
/// its next attempt working that out instead of improving the strategy.
/// </remarks>
[TestClass]
public class SpecValidatorTests : OdysseusTestBase
{
	/// <summary>A complete specification passes.</summary>
	[TestMethod]
	public void AWellFormedSpecificationPasses()
	{
		var validation = SpecValidator.Validate(Breakout());

		IsTrue(validation.IsValid, $"a valid specification was refused: {Describe(validation)}");
	}

	/// <summary>A rule referring to an undeclared parameter is refused, and the refusal lists the real ones.</summary>
	[TestMethod]
	public void AnUndeclaredParameterIsRefusedWithTheDeclaredOnes()
	{
		var spec = Breakout() with
		{
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(new Field(CandleFields.Close), ComparisonOperators.GreaterThan, new ParameterRef("Nonexistent"))),
			],
		};

		var problem = Single(SpecValidator.Validate(spec), "Nonexistent");

		AreEqual("entries.e1.condition", problem.Path);
		IsTrue(problem.Remedy.Contains("BreakoutPeriod", StringComparison.Ordinal),
			"the refusal must list the parameters that do exist.");
	}

	/// <summary>An unknown indicator is refused, and the refusal suggests the nearest real ones.</summary>
	[TestMethod]
	public void AnUnknownIndicatorSuggestsTheNearestNames()
	{
		var spec = Breakout() with
		{
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(new Field(CandleFields.Close), ComparisonOperators.GreaterThan,
						new IndicatorRef("smaa", new Constant(20)))),
			],
		};

		var problem = Single(SpecValidator.Validate(spec), "smaa");

		IsTrue(problem.Remedy.Contains("sma", StringComparison.Ordinal), "the nearest name must be suggested.");
		IsTrue(problem.Remedy.Contains("get_indicator_catalog", StringComparison.Ordinal),
			"the caller must be told where the full list comes from.");
	}

	/// <summary>An indicator that reads a fixed field refuses to be pointed elsewhere.</summary>
	[TestMethod]
	public void AnIndicatorWithNoChoiceRefusesASource()
	{
		var spec = Breakout() with
		{
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(new Field(CandleFields.Close), ComparisonOperators.GreaterThan,
						new IndicatorRef("atr", new Constant(14), CandleFields.Close))),
			],
		};

		var problem = Single(SpecValidator.Validate(spec), "atr");

		IsTrue(problem.Message.Contains("whole candle", StringComparison.Ordinal),
			"the refusal must say what the indicator actually reads.");
	}

	/// <summary>An entry with no matching exit is refused: the position would never be closed.</summary>
	[TestMethod]
	public void AnEntryWithoutAnExitIsRefused()
	{
		var spec = Breakout() with
		{
			Exits = [new("x1", ExitKinds.SessionEnd, TradeDirections.Short)],
		};

		var problem = Single(SpecValidator.Validate(spec), "never closed");

		AreEqual("entries.e1", problem.Path);
		IsTrue(problem.Remedy.Contains("Long", StringComparison.Ordinal), "the refusal must say which direction is missing an exit.");
	}

	/// <summary>A strategy that never closes anything is refused, with the reason it matters.</summary>
	[TestMethod]
	public void AStrategyThatNeverClosesIsRefused()
	{
		var problem = Single(SpecValidator.Validate(Breakout() with { Exits = [] }), "never closes");

		IsTrue(problem.Remedy.Contains("measures the market", StringComparison.Ordinal),
			"the refusal must say why an unclosed position makes the result meaningless.");
	}

	/// <summary>
	/// Too little warm-up is the dangerous one: it does not fail, it silently reports a result that was
	/// partly produced by indicators that had not formed yet.
	/// </summary>
	[TestMethod]
	public void TooLittleWarmupIsRefusedWithTheNumberNeeded()
	{
		var spec = Breakout() with { WarmupBars = 5 };

		var problem = Single(SpecValidator.Validate(spec), "look back");

		AreEqual("warmupBars", problem.Path);
		IsTrue(problem.Remedy.Contains("61", StringComparison.Ordinal),
			"the refusal must state the number of candles required, which is the widest the search may go.");
	}

	/// <summary>
	/// Warm-up is measured against the widest window the optimizer may choose, not against the default,
	/// and it includes the offset: the highest of sixty candles ending one candle ago needs sixty-one.
	/// </summary>
	[TestMethod]
	public void WarmupCoversTheWidestWindowTheSearchMayChoose()
		=> AreEqual(61, SpecValidator.RequiredWarmup(Breakout()),
			"a window declared as a parameter must be covered at its maximum, plus however far back it is read.");

	/// <summary>A parameter whose default lies outside its own range is refused.</summary>
	[TestMethod]
	public void ADefaultOutsideItsRangeIsRefused()
	{
		var spec = Breakout() with
		{
			Parameters = [new("BreakoutPeriod", ParameterTypes.Integer, Default: 200, Minimum: 10, Maximum: 60, Step: 5)],
		};

		var problem = Single(SpecValidator.Validate(spec), "outside the range");

		AreEqual("parameters.BreakoutPeriod", problem.Path);
	}

	/// <summary>Searching too many numbers at once is refused, with the reason.</summary>
	[TestMethod]
	public void TooManySearchedParametersAreRefused()
	{
		var parameters = Enumerable
			.Range(0, SpecValidator.MaxOptimizableParameters + 1)
			.Select(i => new ParameterDefinition($"P{i}", ParameterTypes.Integer, 10, 2, 60, 1))
			.ToList();

		var problem = Single(SpecValidator.Validate(Breakout() with { Parameters = parameters }), "marked for search");

		IsTrue(problem.Remedy.Contains("coincidence", StringComparison.Ordinal),
			"the refusal must say why more dimensions make a result less trustworthy.");
	}

	/// <summary>A rule acting in a direction the strategy forbids is refused.</summary>
	[TestMethod]
	public void ARuleInAForbiddenDirectionIsRefused()
	{
		var spec = Breakout() with { AllowLong = false, AllowShort = true };

		IsTrue(SpecValidator.Validate(spec).Problems.Any(p => p.Message.Contains("not allowed", StringComparison.Ordinal)));
	}

	/// <summary>Dividing by a literal zero is refused before it can crash a run.</summary>
	[TestMethod]
	public void DivisionByZeroIsRefused()
	{
		var spec = Breakout() with
		{
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Arithmetic(ArithmeticOperators.Divide, [new Field(CandleFields.Close), new Constant(0)]),
						ComparisonOperators.GreaterThan,
						new Constant(1))),
			],
		};

		Single(SpecValidator.Validate(spec), "divides by zero");
	}

	/// <summary>Every problem carries a place, a description and a remedy.</summary>
	[TestMethod]
	public void EveryProblemIsActionable()
	{
		var spec = Breakout() with { Name = "", Exits = [], WarmupBars = 1 };
		var validation = SpecValidator.Validate(spec);

		IsTrue(validation.Problems.Count >= 3, "the fixture should produce several problems.");

		foreach (var problem in validation.Problems)
		{
			IsFalse(string.IsNullOrWhiteSpace(problem.Path), "a problem with no path leaves the caller searching.");
			IsFalse(string.IsNullOrWhiteSpace(problem.Message), $"the problem at {problem.Path} says nothing.");
			IsFalse(string.IsNullOrWhiteSpace(problem.Remedy), $"the problem at {problem.Path} suggests nothing.");
		}
	}

	/// <summary>Everything wrong is reported at once, rather than one thing per attempt.</summary>
	[TestMethod]
	public void AllProblemsAreReportedTogether()
	{
		var spec = Breakout() with { Name = "", Risk = new(0m, 0.02m), WarmupBars = 1 };

		var paths = SpecValidator.Validate(spec).Problems.Select(p => p.Path).ToArray();

		IsTrue(paths.Contains("name"));
		IsTrue(paths.Contains("risk.maxPositionPercent"));
		IsTrue(paths.Contains("warmupBars"));
	}

	private static SpecProblem Single(SpecValidation validation, string fragment)
	{
		var matching = validation.Problems
			.Where(p => p.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase))
			.ToArray();

		IsTrue(matching.Length == 1,
			$"expected exactly one problem mentioning '{fragment}', found {matching.Length}: {Describe(validation)}");

		return matching[0];
	}

	private static string Describe(SpecValidation validation)
		=> string.Join(" | ", validation.Problems.Select(p => $"{p.Path}: {p.Message}"));

	private static StrategySpec Breakout()
		=> new()
		{
			Name = "Volume confirmed breakout",
			Thesis = "A close above a recent high has positive short-horizon expectancy when volume confirms it.",
			AllowLong = true,
			AllowShort = false,
			TimeFrame = TimeSpan.FromMinutes(5),
			WarmupBars = 61,
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Field(CandleFields.Close),
						ComparisonOperators.GreaterThan,
						new IndicatorRef("highest", new ParameterRef("BreakoutPeriod"), CandleFields.High, Offset: 1))),
			],
			Exits =
			[
				new("x1", ExitKinds.AtrStop, TradeDirections.Long, Length: new Constant(14), Multiplier: new Constant(2)),
				new("x2", ExitKinds.SessionEnd, TradeDirections.Long),
			],
			Parameters = [new("BreakoutPeriod", ParameterTypes.Integer, Default: 20, Minimum: 10, Maximum: 60, Step: 5)],
			Risk = new(0.10m, 0.02m),
		};
}
