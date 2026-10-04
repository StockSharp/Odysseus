namespace Odysseus.Cli.Console;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Con = System.Console;

/// <summary>
/// Drawing a terminal.
/// </summary>
/// <remarks>
/// A person reads a table, not a JSON document. The same numbers the MCP tools return are laid out in
/// columns here, with the widths measured rather than guessed, so a long symbol or a large figure moves
/// the rule instead of running through it.
/// </remarks>
public static class Ui
{
	private const string Reset = "[0m";
	private const string Dim = "[2m";
	private const string Bold = "[1m";
	private const string Green = "[38;5;72m";
	private const string Red = "[38;5;167m";
	private const string Blue = "[38;5;110m";
	private const string Cyan = "[38;5;79m";

	private static bool _colour = true;

	/// <summary>
	/// Turns colour on or off for the whole run.
	/// </summary>
	/// <param name="on">Whether escape codes may be written.</param>
	public static void UseColour(bool on) => _colour = on;

	/// <summary>Writes a blank line.</summary>
	public static void Blank() => Con.WriteLine();

	/// <summary>
	/// Writes the name of what is about to be shown.
	/// </summary>
	/// <param name="text">Title text.</param>
	/// <param name="note">What it is, in a few words.</param>
	public static void Title(string text, string note = null)
	{
		Con.WriteLine();
		Con.WriteLine(Paint(text.ToUpperInvariant(), Bold + Cyan) + (note is null ? "" : "  " + Paint(note, Dim)));
	}

	/// <summary>
	/// Writes a line of running text.
	/// </summary>
	/// <param name="text">What to say.</param>
	public static void Say(string text) => Con.WriteLine(text);

	/// <summary>
	/// Writes a line that is beside the point but worth having.
	/// </summary>
	/// <param name="text">What to say.</param>
	public static void Aside(string text) => Con.WriteLine(Paint(text, Dim));

	/// <summary>
	/// Writes a refusal.
	/// </summary>
	/// <param name="text">What went wrong.</param>
	public static void Refuse(string text) => Con.Error.WriteLine(Paint("  " + text, Red));

	/// <summary>
	/// Writes a set of named figures side by side.
	/// </summary>
	/// <param name="figures">Label and value of each.</param>
	public static void Figures(params (string Label, string Value)[] figures)
	{
		var widths = figures.Select(f => Math.Max(Visible(f.Label), Visible(f.Value)) + 2).ToArray();

		Con.WriteLine("  " + string.Join("", figures.Select((f, i) => Pad(Paint(f.Value, Bold), widths[i]))));
		Con.WriteLine("  " + string.Join("", figures.Select((f, i) => Pad(Paint(f.Label, Dim), widths[i]))));
	}

	/// <summary>
	/// Writes a table with a rule around it.
	/// </summary>
	/// <param name="headers">Column headings.</param>
	/// <param name="rows">Rows, each with one cell per heading.</param>
	/// <param name="rightAligned">Which columns hold numbers.</param>
	public static void Table(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, params int[] rightAligned)
	{
		var right = new HashSet<int>(rightAligned);

		var widths = headers
			.Select((h, i) => Math.Max(Visible(h), rows.Count == 0 ? 0 : rows.Max(r => Visible(r[i]))))
			.ToArray();

		Rule("┌", "┬", "┐", widths);

		Con.WriteLine("│ " + string.Join(" │ ", headers.Select((h, i) =>
			right.Contains(i) ? PadLeft(Paint(h, Dim), widths[i]) : Pad(Paint(h, Dim), widths[i]))) + " │");

		Rule("├", "┼", "┤", widths);

		foreach (var row in rows)
		{
			Con.WriteLine("│ " + string.Join(" │ ", row.Select((c, i) =>
				right.Contains(i) ? PadLeft(c, widths[i]) : Pad(c, widths[i]))) + " │");
		}

		Rule("└", "┴", "┘", widths);
	}

	/// <summary>
	/// Draws a line through a series of values.
	/// </summary>
	/// <param name="values">The series, oldest first.</param>
	/// <param name="width">Columns to draw it across.</param>
	/// <param name="height">Rows to draw it in.</param>
	/// <param name="baseline">Value at which the line started, marked across the plot.</param>
	/// <remarks>
	/// Blocks rather than braille: eight steps of height per row is enough to see the shape, and every
	/// terminal has them. The series is averaged into the columns available, so a run of ten thousand
	/// points and a run of ten both fill the width.
	/// </remarks>
	public static void Chart(IReadOnlyList<decimal> values, int width, int height, decimal? baseline = null)
	{
		if (values.Count == 0)
		{
			Aside("  (nothing to draw)");
			return;
		}

		var buckets = new decimal[width];

		for (var x = 0; x < width; x++)
		{
			var from = (int)((long)x * values.Count / width);
			var to = (int)((long)(x + 1) * values.Count / width);

			if (to <= from)
				to = from + 1;

			buckets[x] = values.Skip(from).Take(to - from).Average();
		}

		var low = Math.Min(buckets.Min(), baseline ?? buckets.Min());
		var high = Math.Max(buckets.Max(), baseline ?? buckets.Max());
		var span = high - low;

		if (span == 0)
			span = 1;

		const string blocks = " ▁▂▃▄▅▆▇█";

		var rows = new StringBuilder[height];

		for (var y = 0; y < height; y++)
			rows[y] = new StringBuilder();

		foreach (var value in buckets)
		{
			var filled = (double)((value - low) / span) * height;

			for (var y = 0; y < height; y++)
			{
				var fromBottom = height - 1 - y;
				var left = filled - fromBottom;

				var step = left >= 1 ? 8 : left <= 0 ? 0 : (int)Math.Round(left * 8);

				rows[y].Append(blocks[step]);
			}
		}

		var label = Math.Max(Visible(Money(high)), Visible(Money(low)));

		for (var y = 0; y < height; y++)
		{
			var edge = y == 0 ? Money(high) : y == height - 1 ? Money(low) : "";

			Con.WriteLine("  " + Paint(PadLeft(edge, label), Dim) + " │" + Paint(rows[y].ToString(), Blue));
		}

		Con.WriteLine("  " + new string(' ', label) + " └" + new string('─', width));
	}

	/// <summary>
	/// Writes an amount of money, coloured by its sign.
	/// </summary>
	/// <param name="value">The amount.</param>
	/// <returns>The text to print.</returns>
	public static string Signed(decimal value)
		=> Paint((value >= 0 ? "+" : "−") + Math.Abs(value).ToString("N2", CultureInfo.InvariantCulture),
			value >= 0 ? Green : Red);

	/// <summary>
	/// Writes a plain number.
	/// </summary>
	/// <param name="value">The number.</param>
	/// <param name="places">Digits after the point.</param>
	/// <returns>The text to print.</returns>
	public static string Number(decimal value, int places = 2)
		=> value.ToString("N" + places, CultureInfo.InvariantCulture);

	private static string Money(decimal value)
		=> value.ToString("N0", CultureInfo.InvariantCulture);

	private static void Rule(string left, string join, string right, int[] widths)
		=> Con.WriteLine(left + string.Join(join, widths.Select(w => new string('─', w + 2))) + right);

	private static string Paint(string text, string colour)
		=> _colour && text.Length > 0 ? colour + text + Reset : text;

	// Escape codes take no room on screen, so they take none in the measurement either.
	private static int Visible(string text)
	{
		var count = 0;

		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] == '')
			{
				while (i < text.Length && text[i] != 'm')
					i++;

				continue;
			}

			count++;
		}

		return count;
	}

	private static string Pad(string text, int width)
		=> text + new string(' ', Math.Max(0, width - Visible(text)));

	private static string PadLeft(string text, int width)
		=> new string(' ', Math.Max(0, width - Visible(text))) + text;
}
