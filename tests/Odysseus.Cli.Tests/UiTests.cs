namespace Odysseus.Cli.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Cli.Console;
using Odysseus.TestKit;

using Con = System.Console;

/// <summary>
/// Laying numbers out on a terminal.
/// </summary>
/// <remarks>
/// A column is only a column while every row in it starts at the same place. Colour arrives as escape
/// codes that occupy no width on screen and plenty in a string, so anything that measures text by its
/// length pads the coloured rows short and the table comes apart exactly where the interesting numbers
/// are — which is what happened before these existed.
/// </remarks>
// Console output is one thing for the whole process, so these cannot run beside anything that writes.
[DoNotParallelize]
[TestClass]
public class UiTests : OdysseusTestBase
{
	/// <summary>Every line of a table is the same width, whatever colour is in it.</summary>
	[TestMethod]
	public void ColourDoesNotMoveTheColumns()
	{
		var text = Capture(() => Ui.Table(
			["symbol", "result"],
			[
				["NVDA", Ui.Signed(1234.5m)],
				["AAPL", Ui.Signed(-7m)],
				["a much longer symbol", "0.00"],
			],
			1));

		var widths = Bare(text).Select(l => l.Length).Distinct().ToArray();

		AreEqual(1, widths.Length, $"the table came out in {widths.Length} different widths: {string.Join(", ", widths)}.");
	}

	/// <summary>A table draws a rule above the headings, under them, and below the last row.</summary>
	[TestMethod]
	public void ATableIsRuledAround()
	{
		var lines = Bare(Capture(() => Ui.Table(["a", "b"], [["1", "2"]])));

		IsTrue(lines[0].StartsWith('┌') && lines[0].EndsWith('┐'), $"no rule above: {lines[0]}");
		IsTrue(lines[2].StartsWith('├'), $"no rule under the headings: {lines[2]}");
		IsTrue(lines[^1].StartsWith('└') && lines[^1].EndsWith('┘'), $"no rule below: {lines[^1]}");
	}

	/// <summary>A column of numbers is flush right, so the digits line up.</summary>
	[TestMethod]
	public void NumbersAreFlushRight()
	{
		var lines = Bare(Capture(() => Ui.Table(["what", "amount"], [["one", "7"], ["two", "1234"]], 1)));

		var first = lines[3];
		var second = lines[4];

		AreEqual(first.LastIndexOf('7'), second.LastIndexOf('4'), "the last digits do not line up.");
	}

	/// <summary>A rising series is drawn rising.</summary>
	[TestMethod]
	public void TheChartFollowsTheSeries()
	{
		var rising = Enumerable.Range(0, 200).Select(i => 100m + i).ToArray();

		var lines = Bare(Capture(() => Ui.Chart(rising, 40, 6)));

		var top = lines[0];
		var bottom = lines[^2];

		IsTrue(Ink(top[^12..]) > Ink(top[..12]), "the top row is not fuller on the right, so it does not rise.");
		IsTrue(Ink(bottom) > 0, "the bottom row is empty, so the series is floating.");
	}

	/// <summary>The plot is labelled with what it reaches.</summary>
	[TestMethod]
	public void TheChartSaysWhereItGot()
	{
		var text = Capture(() => Ui.Chart([100_000m, 100_500m, 101_000m], 20, 4));

		IsTrue(text.Contains("101,000", StringComparison.Ordinal), $"the highest value is not shown: {text}");
		IsTrue(text.Contains("100,000", StringComparison.Ordinal), $"the lowest value is not shown: {text}");
	}

	/// <summary>A flat series does not divide by nothing.</summary>
	[TestMethod]
	public void AFlatSeriesIsDrawnFlat()
	{
		var lines = Bare(Capture(() => Ui.Chart([100m, 100m, 100m, 100m], 20, 4)));

		AreEqual(5, lines.Length, "a flat series produced the wrong number of rows.");
	}

	/// <summary>Nothing to draw says so rather than throwing.</summary>
	[TestMethod]
	public void AnEmptySeriesSaysSo()
	{
		var text = Capture(() => Ui.Chart([], 20, 4));

		IsTrue(text.Contains("nothing", StringComparison.OrdinalIgnoreCase), $"an empty series printed: {text}");
	}

	/// <summary>Money carries its sign, and a loss is not a negative of a minus.</summary>
	[TestMethod]
	public void MoneyCarriesItsSign()
	{
		AreEqual("+1,234.50", Strip(Ui.Signed(1234.5m)));
		AreEqual("−7.00", Strip(Ui.Signed(-7m)));
		AreEqual("+0.00", Strip(Ui.Signed(0m)));
	}

	private static string Capture(Action write)
	{
		var before = Con.Out;

		using var writer = new StringWriter();

		try
		{
			Con.SetOut(writer);
			Ui.UseColour(true);

			write();
		}
		finally
		{
			Con.SetOut(before);
		}

		return writer.ToString();
	}

	private static string[] Bare(string text)
		=> [.. text.Split('\n').Select(l => Strip(l.TrimEnd('\r'))).Where(l => l.Length > 0)];

	private static string Strip(string text)
		=> System.Text.RegularExpressions.Regex.Replace(text, "\\[[0-9;]*m", "");

	private static int Ink(string row)
		=> row.Count(c => c is >= '▁' and <= '█');
}
