namespace Odysseus.CodeGen.Tests;

using System.IO;

/// <summary>
/// Keeps a copy of what the translator produces, so that a change to it shows up as a change to a file.
/// </summary>
/// <remarks>
/// The other tests assert properties of the generated source: that it waits for warm-up, that rules are
/// traceable, that divisions are guarded. This one keeps the whole thing. What it buys is review: when
/// the translator changes, the effect on real output appears in the diff, where a person can look at it,
/// instead of being implied by a passing test.
///
/// The stored copy is never rewritten to make this pass. A difference is either an improvement worth
/// reading or a mistake worth catching, and both need a person to say which.
/// </remarks>
[TestClass]
public class GoldenSourceTests : OdysseusTestBase
{
	private static string GoldenPath
		=> Path.Combine(RepositoryRoot, "tests", "Odysseus.CodeGen.Tests", "Golden", "volume-confirmed-breakout.cs");

	/// <summary>The translator still produces what was reviewed.</summary>
	[TestMethod]
	public void TheGeneratedSourceMatchesWhatWasReviewed()
	{
		var generated = StrategyTranslator.Translate(Breakout()).Source;

		if (!File.Exists(GoldenPath))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath));
			File.WriteAllText(GoldenPath, generated);

			Fail(
				$"There was no reviewed copy of the generated source, so one was written to {GoldenPath}. " +
				"Read it, and commit it if it is what the translator should produce.");
		}

		var reviewed = File.ReadAllText(GoldenPath).Replace("\r\n", "\n", StringComparison.Ordinal);

		if (reviewed == generated)
			return;

		Fail(
			"The translator no longer produces the source that was reviewed." +
			Environment.NewLine + FirstDifference(reviewed, generated) +
			Environment.NewLine +
			$"If the new output is correct, replace {GoldenPath} deliberately, having read it.");
	}

	private static string FirstDifference(string reviewed, string generated)
	{
		var left = reviewed.Split('\n');
		var right = generated.Split('\n');

		for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
		{
			var before = i < left.Length ? left[i] : "(nothing)";
			var after = i < right.Length ? right[i] : "(nothing)";

			if (before != after)
				return $"First difference at line {i + 1}:{Environment.NewLine}  reviewed:  {before}{Environment.NewLine}  generated: {after}";
		}

		return "The files differ only in length.";
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
					new AllOf(
					[
						new Compare(
							new Field(CandleFields.Close),
							ComparisonOperators.GreaterThan,
							new IndicatorRef("highest", new ParameterRef("BreakoutPeriod"), CandleFields.High, Offset: 1)),
						new Compare(
							new Field(CandleFields.Volume),
							ComparisonOperators.GreaterThan,
							new Arithmetic(ArithmeticOperators.Multiply,
							[
								new IndicatorRef("volumeSma", new ParameterRef("VolumePeriod")),
								new ParameterRef("VolumeMultiplier"),
							])),
						new SessionWindow("09:50:00", "15:45:00"),
					])),
			],
			Exits =
			[
				new("x1", ExitKinds.AtrStop, TradeDirections.Long,
					Length: new ParameterRef("AtrPeriod"), Multiplier: new ParameterRef("AtrMultiplier")),
				new("x2", ExitKinds.SessionEnd, TradeDirections.Long),
			],
			Parameters =
			[
				new("BreakoutPeriod", ParameterTypes.Integer, Default: 20, Minimum: 10, Maximum: 60, Step: 5),
				new("VolumePeriod", ParameterTypes.Integer, Default: 30, Minimum: 10, Maximum: 60, Step: 10),
				new("VolumeMultiplier", ParameterTypes.Decimal, Default: 1.5m, Minimum: 1.1m, Maximum: 3.0m, Step: 0.1m),
				new("AtrPeriod", ParameterTypes.Integer, Default: 14, Minimum: 7, Maximum: 30, Step: 1),
				new("AtrMultiplier", ParameterTypes.Decimal, Default: 2.0m, Minimum: 1.0m, Maximum: 5.0m, Step: 0.5m),
			],
			Risk = new(0.10m, 0.02m),
		};
}
