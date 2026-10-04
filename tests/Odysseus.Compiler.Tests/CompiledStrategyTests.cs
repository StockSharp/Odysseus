namespace Odysseus.Compiler.Tests;

using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using StockSharp.Algo.Strategies;

using Odysseus.CodeGen;
using Odysseus.Compiler;
using Odysseus.Platform;
using Odysseus.Spec;
using Odysseus.TestKit;

using Odysseus.Domain;

/// <summary>
/// The translator and the compiler together.
/// </summary>
/// <remarks>
/// This is where the translator is really tested. Every other test of it compares strings; this one
/// asks the C# compiler whether what came out is a program, which is the only opinion that counts.
/// </remarks>
[TestClass]
public class CompiledStrategyTests : OdysseusTestBase
{
	// Everything a generated strategy is written against: the engine, its message and entity types, and
	// the base library they are built on. Named by the same code the server names them with, so this
	// suite cannot pass against a set the product does not use.
	private static StrategyCompiler Compiler => new(EngineAssemblies.Paths(AppContext.BaseDirectory));

	/// <summary>A translated specification compiles.</summary>
	[TestMethod]
	public void ATranslatedSpecificationCompiles()
	{
		var translated = StrategyTranslator.Translate(Breakout());
		var outcome = Compiler.Compile(translated.Source, "candidate");

		IsTrue(outcome.Succeeded, Describe(outcome, translated.Source));
	}

	/// <summary>Nothing the translator emits produces so much as a warning.</summary>
	[TestMethod]
	public void TranslatedSourceIsClean()
	{
		var translated = StrategyTranslator.Translate(Breakout());
		var outcome = Compiler.Compile(translated.Source, "candidate");

		AreEqual(0, outcome.Messages.Count,
			$"the generated source is not clean: {string.Join(" | ", outcome.Messages.Select(m => $"{m.Id} {m.Message}"))}");
	}

	/// <summary>
	/// Every shape the specification language allows must compile, not only the one in the fixture. A
	/// node that translates to something the compiler rejects would be discovered by an agent, at the
	/// cost of one of its attempts.
	/// </summary>
	[TestMethod]
	public void EveryExpressionShapeCompiles()
	{
		Expression[] conditions =
		[
			new AllOf([new PositionIs(PositionStates.Flat), new Not(new PositionIs(PositionStates.Long))]),
			new AnyOf([new Compare(new Field(CandleFields.Close), ComparisonOperators.LessOrEqual, new Constant(10))]),
			new Compare(
				new Arithmetic(ArithmeticOperators.Add, [new Field(CandleFields.High), new Field(CandleFields.Low)]),
				ComparisonOperators.GreaterThan,
				new Arithmetic(ArithmeticOperators.Multiply, [new Field(CandleFields.Close), new Constant(2)])),
			new Compare(
				new Arithmetic(ArithmeticOperators.Divide, [new Field(CandleFields.Close), new ParameterRef("BreakoutPeriod")]),
				ComparisonOperators.GreaterThan,
				new Constant(1)),
			new Compare(
				new AbsoluteValue(new Arithmetic(ArithmeticOperators.Subtract, [new Field(CandleFields.Close), new Field(CandleFields.Open)])),
				ComparisonOperators.GreaterThan,
				new Constant(1)),
			new Compare(
				new Arithmetic(ArithmeticOperators.Min, [new Field(CandleFields.Close), new Field(CandleFields.Open)]),
				ComparisonOperators.LessThan,
				new Arithmetic(ArithmeticOperators.Max, [new Field(CandleFields.High), new Field(CandleFields.Low)])),
			new Cross(new Field(CandleFields.Close), CrossDirections.Above,
				new IndicatorRef("ema", new Constant(20), CandleFields.Close)),
			new Cross(new IndicatorRef("rsi", new Constant(14), CandleFields.Close), CrossDirections.Below,
				new Constant(70)),
			new SessionWindow("09:50:00", "15:45:00"),
			new Compare(new IndicatorRef("stdDev", new Constant(20), CandleFields.Close),
				ComparisonOperators.GreaterThan, new IndicatorRef("atr", new Constant(14))),
			new Compare(new Field(CandleFields.Volume), ComparisonOperators.GreaterThan,
				new IndicatorRef("volumeSma", new Constant(30))),
			new Compare(new Field(CandleFields.Close, Offset: 3), ComparisonOperators.GreaterThan,
				new IndicatorRef("lowest", new Constant(20), CandleFields.Low, Offset: 2)),
		];

		foreach (var condition in conditions)
		{
			var spec = Breakout() with
			{
				WarmupBars = 200,
				Entries = [new("e1", TradeDirections.Long, condition)],
			};

			var translated = StrategyTranslator.Translate(spec);
			var outcome = Compiler.Compile(translated.Source, "candidate");

			IsTrue(outcome.Succeeded, $"{condition.GetType().Name} did not compile. {Describe(outcome, translated.Source)}");
			AreEqual(0, outcome.Messages.Count, $"{condition.GetType().Name} compiled with warnings.");
		}
	}

	/// <summary>Every exit shape compiles as well.</summary>
	[TestMethod]
	public void EveryExitShapeCompiles()
	{
		ExitRule[] exits =
		[
			new("x", ExitKinds.SessionEnd, TradeDirections.Long),
			new("x", ExitKinds.TimeExit, TradeDirections.Long, Length: new Constant(10)),
			new("x", ExitKinds.AtrStop, TradeDirections.Long, Length: new Constant(14), Multiplier: new Constant(2)),
			new("x", ExitKinds.AtrTarget, TradeDirections.Long, Length: new Constant(14), Multiplier: new Constant(3)),
			new("x", ExitKinds.Condition, TradeDirections.Long,
				Condition: new Compare(new Field(CandleFields.Close), ComparisonOperators.LessThan, new Constant(1))),
		];

		foreach (var exit in exits)
		{
			var translated = StrategyTranslator.Translate(Breakout() with { Exits = [exit] });
			var outcome = Compiler.Compile(translated.Source, "candidate");

			IsTrue(outcome.Succeeded, $"{exit.Kind} did not compile. {Describe(outcome, translated.Source)}");
		}
	}

	/// <summary>
	/// The compiled assembly is a function of the source alone, so a candidate can be identified by its
	/// hash rather than by when it was built.
	/// </summary>
	[TestMethod]
	public void CompilationIsDeterministic()
	{
		var translated = StrategyTranslator.Translate(Breakout());

		var first = Compiler.Compile(translated.Source, "candidate");
		var second = Compiler.Compile(translated.Source, "candidate");

		AreEqual(first.AssemblyHash, second.AssemblyHash,
			"the same source produced two different assemblies, so a candidate is not identified by what it is.");
	}

	/// <summary>The compiled type really is a strategy, and can be created.</summary>
	[TestMethod]
	public void TheCompiledTypeIsAStrategy()
	{
		var translated = StrategyTranslator.Translate(Breakout());
		var outcome = Compiler.Compile(translated.Source, "candidate");

		IsTrue(outcome.Succeeded, Describe(outcome, translated.Source));

		var type = Assembly.Load(outcome.Assembly)
			.GetTypes()
			.Single(t => typeof(Strategy).IsAssignableFrom(t) && !t.IsAbstract);

		AreEqual(translated.ClassName, type.Name);

		IsNotNull(Activator.CreateInstance(type), "the generated strategy cannot be created.");
	}

	/// <summary>
	/// Reaching outside what a strategy is given is refused. This is not protection from a hostile
	/// author — the code belongs to whoever runs it — it is protection of the result: a strategy that
	/// reads the wall clock or draws an unseeded number returns a different answer tomorrow on the same
	/// data, and every metric computed from it becomes a number nobody can check.
	/// </summary>
	[TestMethod]
	public void ReachingOutsideTheContextIsRefused()
	{
		TheFixtureCompilesWithNothingWrongInIt();

		// Every attempt here sits in an assembly a strategy legitimately needs, so no reference set could
		// exclude it and a named rule has to. Both ways of reaching the same thing are listed, because a
		// rule that only knew constructors would let the shared instance and the static member through.
		(string Attempt, string Rule)[] attempts =
		[
			("var now = System.DateTime.Now;", "ODSTR012"),
			("var now = System.DateTime.UtcNow;", "ODSTR012"),
			("var id = System.Guid.NewGuid();", "ODSTR013"),
			("var drawn = new System.Random().Next();", "ODSTR013"),
			("var drawn = System.Random.Shared.Next();", "ODSTR013"),
			("var machine = System.Environment.MachineName;", "ODSTR012"),
			("var text = System.IO.File.ReadAllText(\"c:/secrets.txt\");", "ODSTR012"),
			("var elapsed = new System.Diagnostics.Stopwatch();", "ODSTR012"),
			("var gate = new System.Threading.Lock();", "ODSTR012"),
			("var encoded = System.Net.WebUtility.UrlEncode(\"x\");", "ODSTR012"),
			("var running = System.Reflection.Assembly.GetExecutingAssembly();", "ODSTR012"),
			("var today = System.DateTime.Today;", "ODSTR012"),
			("var now = System.DateTimeOffset.Now;", "ODSTR012"),
			("var now = System.DateTimeOffset.UtcNow;", "ODSTR012"),
			("var ticks = System.Environment.TickCount64;", "ODSTR012"),
			("var hash = \"AAPL\".GetHashCode();", "ODSTR013"),
			("var now = Ecng.Common.TimeHelper.Now;", "ODSTR012"),
			("var drawn = Ecng.Common.RandomGen.GetInt();", "ODSTR013"),
		];

		var compiler = Compiler;

		foreach (var (attempt, rule) in attempts)
		{
			var source = Reaching(attempt);
			var outcome = compiler.Compile(source, "reacher");

			IsFalse(outcome.Succeeded, $"'{attempt}' compiled, so a strategy can reach outside what it is given.");

			var message = outcome.Messages.FirstOrDefault(m => m.Id == rule);

			IsNotNull(message, $"'{attempt}' was refused, but not by {rule}: {string.Join(" | ", outcome.Messages.Select(m => m.Id))}");

			IsTrue(message.Message.Contains("instead", StringComparison.Ordinal),
				"a refusal has to say what to use instead, or the next attempt is a guess.");
		}
	}

	/// <summary>
	/// Reading a source for the rules, without building it, finds nothing in what the translator writes and
	/// finds a clock where one was spliced in, with the line it is on.
	/// </summary>
	[TestMethod]
	public void InspectingASourceFindsWhatItBreaks()
	{
		var builder = new StrategyBuilder(EngineAssemblies.Paths(AppContext.BaseDirectory));

		AreEqual(0, builder.Inspect(StrategyTranslator.Translate(Breakout()).Source).Count,
			"the translator's own output breaks a rule.");

		var source = Reaching("var now = System.DateTime.Now;");
		var problems = builder.Inspect(source);

		AreEqual(1, problems.Count, string.Join(" | ", problems.Select(p => $"{p.Rule} {p.Message}")));
		AreEqual("ODSTR012", problems[0].Rule);

		var line = source.Split('\n').TakeWhile(l => !l.Contains("DateTime.Now", StringComparison.Ordinal)).Count() + 1;

		AreEqual(line, problems[0].Line, "the problem was placed on a line other than the one it is on.");
	}

	/// <summary>
	/// A member reached without naming its type is the same member. Importing a clock with a using static
	/// directive and reading it by its bare name reads the machine clock all the same.
	/// </summary>
	[TestMethod]
	public void AClockReachedThroughUsingStaticIsRefused()
	{
		TheFixtureCompilesWithNothingWrongInIt();

		var source = "using static System.DateTime;" + Environment.NewLine + Reaching("var now = Now;");
		var outcome = Compiler.Compile(source, "reacher");

		IsFalse(outcome.Succeeded, "a clock imported with using static compiled.");
		IsTrue(outcome.Messages.Any(m => m.Id == "ODSTR012"), string.Join(" | ", outcome.Messages.Select(m => $"{m.Id} {m.Message}")));
	}

	/// <summary>
	/// What the reference set does not carry cannot be named at all, and it is the compiler that says
	/// so. The two refusals are worth keeping apart: a rule that spoke about a type the compilation
	/// never resolved would name something that is not there to be used.
	/// </summary>
	[TestMethod]
	public void WhatTheReferenceSetLeavesOutIsRefusedByTheCompiler()
	{
		TheFixtureCompilesWithNothingWrongInIt();

		string[] attempts =
		[
			"System.Threading.Thread.Sleep(1);",
			"var size = System.Runtime.InteropServices.Marshal.SizeOf<int>();",
			"var client = new System.Net.Http.HttpClient();",
		];

		var compiler = Compiler;

		foreach (var attempt in attempts)
		{
			var source = Reaching(attempt);
			var outcome = compiler.Compile(source, "reacher");

			IsFalse(outcome.Succeeded, $"'{attempt}' compiled, so the reference set carries more than it was given.");

			IsNotNull(outcome.Messages.FirstOrDefault(m => m.Id == "CS0234"),
				$"'{attempt}' was refused, but not as a name that does not exist: {string.Join(" | ", outcome.Messages.Select(m => m.Id))}");

			IsFalse(outcome.Messages.Any(m => m.Id.StartsWith("ODSTR", StringComparison.Ordinal)),
				$"'{attempt}' was refused by a rule naming a type the compilation never resolved.");
		}
	}

	// The attempt is spliced into the translator's own output rather than into a class written here by
	// hand: a fixture that names types the translator does not emit fails to compile on its own, and a
	// refusal of the fixture says nothing about the attempt inside it.
	private static string Reaching(string attempt)
	{
		const string counted = "\t\t_barsSeen++;\n";

		var source = StrategyTranslator.Translate(Breakout()).Source;

		IsTrue(source.Contains(counted, StringComparison.Ordinal),
			"the translator no longer counts bars in ProcessCandle, so there is nowhere to splice the attempt.");

		return source.Replace(counted, $"\t\t{attempt}\n{counted}", StringComparison.Ordinal);
	}

	// The control every refusal above is measured against. Until the same fixture carrying a harmless
	// statement is known to compile, a failed compilation proves nothing about what was spliced in.
	private static void TheFixtureCompilesWithNothingWrongInIt()
	{
		var source = Reaching("var range = candle.HighPrice - candle.LowPrice;");
		var outcome = Compiler.Compile(source, "reacher");

		IsTrue(outcome.Succeeded, $"the fixture the attempts are spliced into does not compile. {Describe(outcome, source)}");
	}

	private static string Describe(CompilationOutcome outcome, string source)
	{
		var messages = string.Join(
			Environment.NewLine,
			outcome.Messages.Select(m => $"  {m.Severity} {m.Id} at {m.Line}:{m.Column}: {m.Message}"));

		var numbered = string.Join(
			Environment.NewLine,
			source.Split('\n').Select((line, index) => $"{index + 1,4}: {line}"));

		return $"{Environment.NewLine}{messages}{Environment.NewLine}--- source ---{Environment.NewLine}{numbered}";
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
