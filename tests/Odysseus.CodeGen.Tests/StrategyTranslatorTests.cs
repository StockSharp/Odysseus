namespace Odysseus.CodeGen.Tests;

using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.CodeGen;
using Odysseus.Spec;
using Odysseus.TestKit;

using Odysseus.Domain;

/// <summary>
/// Turning a checked specification into C#.
/// </summary>
/// <remarks>
/// The translator is a compiler, not a generator, and the tests hold it to that: the same
/// specification must produce the same bytes, everywhere and always. Without it a candidate cannot be
/// identified by the hash of its source, a result cannot be re-derived, and the difference between two
/// candidates stops being the difference between two specifications.
/// </remarks>
[TestClass]
public class StrategyTranslatorTests : OdysseusTestBase
{
	/// <summary>The same specification produces the same source, byte for byte.</summary>
	[TestMethod]
	public void TranslationIsDeterministic()
	{
		var first = StrategyTranslator.Translate(Breakout());
		var second = StrategyTranslator.Translate(Breakout());

		AreEqual(first.Source, second.Source);
		AreEqual(first.SourceHash, second.SourceHash);
	}

	/// <summary>
	/// Line endings are written rather than inherited, or the same specification would hash differently
	/// on different machines and a candidate would stop being itself depending on where it was built.
	/// </summary>
	[TestMethod]
	public void LineEndingsDoNotDependOnTheMachine()
	{
		var source = StrategyTranslator.Translate(Breakout()).Source;

		IsFalse(source.Contains('\r'), "the source carries a carriage return, so its hash depends on the platform.");
	}

	/// <summary>A change to what the strategy does is a change to the source.</summary>
	[TestMethod]
	public void AStructuralChangeProducesDifferentSource()
	{
		var original = StrategyTranslator.Translate(Breakout());

		var altered = Breakout() with
		{
			Exits = [new("x2", ExitKinds.SessionEnd, TradeDirections.Long)],
		};

		AreNotEqual(original.SourceHash, StrategyTranslator.Translate(altered).SourceHash);
	}

	/// <summary>
	/// Rewording a specification leaves the candidate it already is. A candidate is identified by what it
	/// does, and a project may build only so many of them, so a renamed strategy or a rewritten thesis
	/// must not spend a second one. The words still go into the generated file — they are what makes it
	/// readable next to the specification — but the identity is not taken over them.
	/// </summary>
	[TestMethod]
	public void RewordingASpecificationLeavesTheStrategyItAlreadyIs()
	{
		var original = StrategyTranslator.Translate(Breakout());

		var reworded = StrategyTranslator.Translate(Breakout() with
		{
			Name = "Participation confirmed breakout",
			Thesis = "The same rules, described differently.",
		});

		AreEqual(original.SourceHash, reworded.SourceHash,
			"a specification reworded without changing a rule became a second candidate.");

		IsTrue(reworded.Source.Contains("Participation confirmed breakout", StringComparison.Ordinal),
			"the name is missing from the source, so the identity was bought by dropping what people read.");

		IsTrue(reworded.Source.Contains("The same rules, described differently.", StringComparison.Ordinal),
			"the thesis is missing from the source, so the identity was bought by dropping what people read.");

		AreEqual("Participation_confirmed_breakout", reworded.ClassName,
			"the generated class stopped following the name it was given.");
	}

	/// <summary>
	/// Two specifications worded identically but asking for different things are two strategies. This is
	/// the half of the identity that rewording must not cost: what survives is the rules, not the prose.
	/// </summary>
	[TestMethod]
	public void ChangingARuleUnderTheSameWordsIsADifferentStrategy()
	{
		var original = StrategyTranslator.Translate(Breakout());

		var altered = StrategyTranslator.Translate(Breakout() with
		{
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Field(CandleFields.Close),
						ComparisonOperators.GreaterOrEqual,
						new IndicatorRef("highest", new ParameterRef("BreakoutPeriod"), CandleFields.High, Offset: 1))),
			],
		});

		AreNotEqual(original.SourceHash, altered.SourceHash,
			"an entry that also fires on a touch of the high is the same candidate as one that needs a break of it.");
	}

	/// <summary>
	/// Changing what the strategy risks is a change to the source, and therefore a different candidate.
	/// </summary>
	/// <remarks>
	/// The position size is the risk block: it decides how much of the account one position takes, so it
	/// changes every figure a run reports. Nothing in the generated code used to depend on it, which made
	/// two such specifications one candidate - and the second request then returned the first one's
	/// numbers, measured at a size nobody asked for, with nothing in the answer saying so.
	/// </remarks>
	[TestMethod]
	public void ChangingWhatIsRiskedProducesDifferentSource()
	{
		var original = StrategyTranslator.Translate(Breakout());

		var riskier = StrategyTranslator.Translate(Breakout() with { Risk = new(0.25m, 0.02m) });

		AreNotEqual(original.SourceHash, riskier.SourceHash,
			"a strategy that risks two and a half times as much per position translated to the same source.");

		var tighter = StrategyTranslator.Translate(Breakout() with { Risk = new(0.10m, 0.01m) });

		AreNotEqual(original.SourceHash, tighter.SourceHash,
			"halving the daily loss limit translated to the same source.");
	}

	/// <summary>
	/// Retuning a parameter leaves the rules alone. A strategy declares its parameters with a starting
	/// value, as any hand-written one does, so a different starting value is a different file — but the
	/// difference is confined to the declaration. The rules themselves ask for the parameter rather than
	/// carrying the number, which is what lets a search over eight parameters run against one body of
	/// code instead of eight hundred variants of it.
	/// </summary>
	[TestMethod]
	public void RetuningAParameterLeavesTheRulesAlone()
	{
		var original = StrategyTranslator.Translate(Breakout());

		var retuned = StrategyTranslator.Translate(Breakout() with
		{
			Parameters = [new("BreakoutPeriod", ParameterTypes.Integer, Default: 25, Minimum: 10, Maximum: 60, Step: 5)],
		});

		AreNotEqual(original.SourceHash, retuned.SourceHash);
		AreEqual(RulesOf(original.Source), RulesOf(retuned.Source),
			"the rules changed along with the starting value, so the number leaked out of the declaration.");
	}

	private static int Occurrences(string text, string part)
		=> (text.Length - text.Replace(part, string.Empty, StringComparison.Ordinal).Length) / part.Length;

	/// <summary>The part of the generated source that decides what to do, without the declarations.</summary>
	private static string RulesOf(string source)
		=> source[source.IndexOf("private void ProcessCandle", StringComparison.Ordinal)..];

	/// <summary>
	/// Widening the range a parameter may be searched over does change what the code has to wait for,
	/// because warm-up is sized for the widest window the search can pick.
	/// </summary>
	[TestMethod]
	public void WideningASearchRangeChangesTheWarmup()
	{
		var wider = Breakout() with
		{
			WarmupBars = 121,
			Parameters = [new("BreakoutPeriod", ParameterTypes.Integer, Default: 20, Minimum: 10, Maximum: 120, Step: 5)],
		};

		var source = StrategyTranslator.Translate(wider).Source;

		IsTrue(source.Contains("_barsSeen < 121", StringComparison.Ordinal),
			"the generated code still waits for the old warm-up, so a wider search would trade on unformed indicators.");
	}

	/// <summary>
	/// The code is meant to be read next to the specification, so every rule says which one it is.
	/// </summary>
	[TestMethod]
	public void EveryRuleIsTraceableToTheSpecification()
	{
		var source = StrategyTranslator.Translate(Breakout()).Source;

		IsTrue(source.Contains("// entries.e1", StringComparison.Ordinal), "the entry rule is not traceable.");
		IsTrue(source.Contains("// exits.x1", StringComparison.Ordinal), "the stop is not traceable.");
		IsTrue(source.Contains("// exits.x2", StringComparison.Ordinal), "the session exit is not traceable.");
	}

	/// <summary>Nothing acts before the indicators have formed.</summary>
	[TestMethod]
	public void NothingActsBeforeWarmupIsComplete()
	{
		var source = StrategyTranslator.Translate(Breakout()).Source;

		IsTrue(source.Contains("_barsSeen < 61", StringComparison.Ordinal),
			"the generated code does not wait for warm-up, so its first trades come from unformed indicators.");
	}

	/// <summary>Exits are considered before entries, or a position could be opened and closed on one candle.</summary>
	[TestMethod]
	public void ExitsAreConsideredBeforeEntries()
	{
		var source = StrategyTranslator.Translate(Breakout()).Source;

		IsTrue(source.IndexOf("// exits.x1", StringComparison.Ordinal) < source.IndexOf("// entries.e1", StringComparison.Ordinal),
			"an entry evaluated before the exits would let one candle both close and reopen a position.");
	}

	/// <summary>
	/// Indicators are told the value is final. An indicator fed provisional values never reports itself
	/// formed, and a strategy that waits for readiness that never arrives runs to the end of the history
	/// and trades nothing — which reads as a hypothesis with no signal rather than as a defect.
	/// </summary>
	[TestMethod]
	public void IndicatorsAreFedFinalValues()
	{
		var source = StrategyTranslator.Translate(Breakout()).Source;

		AreEqual(
			Occurrences(source, "new DecimalIndicatorValue") + Occurrences(source, "new CandleIndicatorValue"),
			Occurrences(source, "{ IsFinal = true }"),
			"an indicator is fed a value that does not say the candle had finished.");
	}

	/// <summary>Two rules asking for the same indicator share one instance.</summary>
	[TestMethod]
	public void IdenticalIndicatorsAreRequestedOnce()
	{
		var spec = Breakout() with
		{
			Entries =
			[
				new("e1", TradeDirections.Long,
					new AllOf(
					[
						new Compare(new Field(CandleFields.Close), ComparisonOperators.GreaterThan,
							new IndicatorRef("sma", new Constant(20), CandleFields.Close)),
						new Compare(new Field(CandleFields.Open), ComparisonOperators.GreaterThan,
							new IndicatorRef("sma", new Constant(20), CandleFields.Close)),
					])),
			],
		};

		var translated = StrategyTranslator.Translate(spec);

		AreEqual(1, translated.Indicators.Count(i => i.Value == "sma"),
			"the same indicator was requested twice, so the runtime would compute the same series twice.");
	}

	/// <summary>Indicators differing in any way are separate instances.</summary>
	[TestMethod]
	public void DifferentIndicatorsAreRequestedSeparately()
	{
		var spec = Breakout() with
		{
			Entries =
			[
				new("e1", TradeDirections.Long,
					new AllOf(
					[
						new Compare(new Field(CandleFields.Close), ComparisonOperators.GreaterThan,
							new IndicatorRef("sma", new Constant(20), CandleFields.Close)),
						new Compare(new Field(CandleFields.Open), ComparisonOperators.GreaterThan,
							new IndicatorRef("sma", new Constant(50), CandleFields.Close)),
					])),
			],
		};

		AreEqual(2, StrategyTranslator.Translate(spec).Indicators.Count(i => i.Value == "sma"));
	}

	/// <summary>
	/// A division whose divisor could be zero is guarded. A crash halfway through a run costs the whole
	/// run, and the search will eventually land on the value that causes it.
	/// </summary>
	[TestMethod]
	public void DivisionIsGuarded()
	{
		var spec = Breakout() with
		{
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Compare(
						new Arithmetic(ArithmeticOperators.Divide,
							[new Field(CandleFields.Close), new ParameterRef("BreakoutPeriod")]),
						ComparisonOperators.GreaterThan,
						new Constant(1))),
			],
		};

		var source = StrategyTranslator.Translate(spec).Source;

		IsTrue(source.Contains("== 0m ?", StringComparison.Ordinal),
			"a divisor that the search can drive to zero is not guarded.");
	}

	/// <summary>A crossing compares this candle with the previous one on both sides.</summary>
	[TestMethod]
	public void ACrossingLooksAtThePreviousCandle()
	{
		var spec = Breakout() with
		{
			Entries =
			[
				new("e1", TradeDirections.Long,
					new Cross(
						new Field(CandleFields.Close),
						CrossDirections.Above,
						new IndicatorRef("sma", new Constant(20), CandleFields.Close))),
			],
		};

		var source = StrategyTranslator.Translate(spec).Source;

		IsTrue(source.Contains("_seriesCandleClose.At(1)", StringComparison.Ordinal),
			"the crossing does not compare against the previous candle, so it fires on every candle above the line.");

		IsTrue(source.Contains(".At(1)", StringComparison.Ordinal),
			"the crossing does not compare the indicator against its own previous value.");
	}

	/// <summary>An average-range exit asks for the indicator it is defined in terms of.</summary>
	[TestMethod]
	public void AnAverageRangeExitRequestsItsIndicator()
	{
		var translated = StrategyTranslator.Translate(Breakout());

		IsTrue(translated.Indicators.Any(i => i.Value == "atr"),
			"the stop is defined in terms of the average range but never asks for it.");
	}

	/// <summary>An unchecked specification is refused rather than translated into something plausible.</summary>
	[TestMethod]
	public void AnUncheckedSpecificationIsRefused()
	{
		var broken = Breakout() with { Exits = [] };

		var error = Throws<InvalidOperationException>(() => StrategyTranslator.Translate(broken));

		IsTrue(error.Message.Contains("checked", StringComparison.Ordinal));
		IsTrue(error.Message.Contains("exits", StringComparison.Ordinal), "the refusal must carry what was unresolved.");
	}

	/// <summary>The class name comes from the strategy name and is always a legal identifier.</summary>
	[TestMethod]
	public void TheClassNameIsAlwaysLegal()
	{
		AreEqual("Volume_confirmed_breakout", StrategyTranslator.Translate(Breakout()).ClassName);

		AreEqual("S3_way_split", StrategyTranslator.Translate(Breakout() with { Name = "3-way split!" }).ClassName);
	}

	private static StrategySpec Breakout()
		=> new()
		{
			Name = "Volume confirmed breakout",
			Thesis = "A close above a recent high carries on when participation confirms it.",
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
