namespace Odysseus.Products;

using System.Text.RegularExpressions;

using Odysseus.Application;

/// <summary>
/// What one invocation printed, once it has been read.
/// </summary>
/// <param name="Products">The products its output named, in the order they were printed.</param>
/// <param name="Unparsed">Every other line, in order and unaltered but for the log prefix.</param>
public sealed record InstallerReading(IReadOnlyList<Product> Products, IReadOnlyList<string> Unparsed);

/// <summary>
/// Reads what the installer console printed.
/// </summary>
/// <remarks>
/// The program has no machine-readable output. It writes one ad-hoc line shape for a product, wrapped
/// in a log prefix of a time, a component and two bars, and everything else it has to say - progress,
/// warnings, the text of an exception - comes out of the same pipe in the same shape. Errors are on
/// standard output too; nothing is ever written to standard error.
///
/// So the contract here is deliberately small and stated in full: a line that matches the product shape
/// becomes a product, the two sentences that mean an empty list become an empty list, and everything
/// else comes back as text. Nothing is guessed at and nothing is dropped, because a listing that
/// quietly loses the entries it could not read is worse than one that hands them over as prose.
/// </remarks>
public static class InstallerOutput
{
	/// <summary>The sentence a product listing prints when nothing matched.</summary>
	public const string NoProductsMatched = "No products found. Try change criteria.";

	/// <summary>The sentence an installed listing prints when nothing matched.</summary>
	public const string NoProducts = "No products found.";

	/// <summary>The sentence an updates listing prints when nothing matched.</summary>
	public const string NoUpdates = "No updates found.";

	/// <summary>The heading a non-empty listing prints before its entries.</summary>
	public const string Heading = "Products:";

	/// <summary>What the hardware-id command writes before the identifier.</summary>
	public const string HardwareIdLabel = "Hardware ID:";

	// 14:22:33.123 | Installer.Console | the message. The component is padded to a fixed width, and a
	// message can hold newlines - a listing is one message - so a line without the prefix is a
	// continuation rather than something to throw away.
	private static readonly Regex _prefix = new(
		@"^\d{2}:\d{2}:\d{2}\.\d{3} \| [^|]*\| ",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	// id=9, package_id=StockSharp.Designer: (StandaloneApp) "Designer" dir='c:\apps\designer', updates=1.2.3
	// The directory and the update are printed only when there is one.
	private static readonly Regex _entry = new(
		"""^id=(?<id>\d+), package_id=(?<package>[^:]+): \((?<kind>[^)]*)\) "(?<name>[^"]*)"(?: dir='(?<dir>[^']*)')?(?:, updates=(?<updates>.*))?$""",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	// Escape sequences, in case the program is ever run somewhere it decides it is talking to a terminal.
	private static readonly Regex _ansi = new(
		@"\x1B\[[0-9;?]*[ -/]*[@-~]",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>
	/// Reads an invocation's output.
	/// </summary>
	/// <param name="output">Everything the console printed, which may be nothing.</param>
	/// <returns>The products it named and every line that was not one.</returns>
	public static InstallerReading Read(string output)
	{
		var products = new List<Product>();
		var unparsed = new List<string>();

		foreach (var line in Lines(output))
		{
			if (TryParse(line, out var product))
			{
				products.Add(product);

				continue;
			}

			// A heading with nothing under it, or one of the two sentences that mean an empty list. None
			// of the three is a failure and none of them says anything a caller has to read.
			if (line == Heading || line == NoProducts || line == NoProductsMatched || line == NoUpdates)
				continue;

			unparsed.Add(line);
		}

		return new(products, unparsed);
	}

	/// <summary>
	/// Reads the identifier the hardware-id command printed.
	/// </summary>
	/// <param name="output">Everything the console printed.</param>
	/// <returns>The identifier, or an empty string when the output did not carry one.</returns>
	/// <remarks>
	/// Written with no trailing newline, because that command writes markup rather than a line, so the
	/// whole of the output is trimmed rather than split on lines and taken by position.
	/// </remarks>
	public static string HardwareId(string output)
	{
		foreach (var line in Lines(output))
		{
			var label = line.IndexOf(HardwareIdLabel, StringComparison.OrdinalIgnoreCase);

			if (label < 0)
				continue;

			var identifier = line[(label + HardwareIdLabel.Length)..].Trim();

			if (identifier.Length > 0)
				return identifier;
		}

		return string.Empty;
	}

	/// <summary>
	/// Whether the output says another installer holds this machine.
	/// </summary>
	/// <param name="output">Everything the console printed.</param>
	/// <returns><see langword="true"/> when it names the machine-wide conflict.</returns>
	/// <remarks>
	/// A phrase match, and allowed to be wrong. Being wrong means a caller is told a conflict where
	/// there was some other failure, which costs it one wasted wait; not matching at all means the one
	/// failure with an obvious remedy arrives as a generic one. Both phrases are hard-coded English in
	/// the vendor's source rather than localised text.
	/// </remarks>
	public static bool NamesAForeignInstaller(string output)
		=> !string.IsNullOrEmpty(output) &&
			(output.Contains("must be closed", StringComparison.OrdinalIgnoreCase) ||
				output.Contains("installer is already running", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Takes the end of a list of lines.
	/// </summary>
	/// <param name="lines">Lines to take from.</param>
	/// <param name="count">How many to keep.</param>
	/// <returns>The last lines, or all of them when there are fewer.</returns>
	/// <remarks>
	/// The whole of the output is on disk and the answer carries the end of it, which is the same
	/// arrangement the worker's diagnostics use: the last thing a program said before it stopped is
	/// almost always the thing worth reading, and a failure that emits a hundred thousand lines must
	/// not become an answer of a hundred thousand lines.
	/// </remarks>
	public static IReadOnlyList<string> Tail(IReadOnlyList<string> lines, int count)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(count);

		if (lines is null || lines.Count == 0 || count == 0)
			return [];

		if (lines.Count <= count)
			return lines;

		return [.. lines.Skip(lines.Count - count)];
	}

	/// <summary>
	/// Splits output into the lines this reader works on: escape sequences removed, the log prefix
	/// taken off, and nothing empty.
	/// </summary>
	/// <param name="output">Everything the console printed.</param>
	/// <returns>The lines, in order.</returns>
	private static IEnumerable<string> Lines(string output)
	{
		if (string.IsNullOrWhiteSpace(output))
			yield break;

		foreach (var raw in _ansi.Replace(output, string.Empty).Split('\n'))
		{
			var line = _prefix.Replace(raw.TrimEnd('\r').Trim(), string.Empty).Trim();

			if (line.Length > 0)
				yield return line;
		}
	}

	/// <summary>
	/// Reads one line as a product, when it is one.
	/// </summary>
	/// <param name="line">Line to read, with the log prefix already off it.</param>
	/// <param name="product">The product the line named.</param>
	/// <returns><see langword="true"/> when the line was a product entry.</returns>
	/// <remarks>
	/// An identifier too large to be a number is a line that looked like an entry and is not one, so it
	/// goes back to the caller as text rather than raising: this reader never throws on what a program
	/// happened to print.
	/// </remarks>
	private static bool TryParse(string line, out Product product)
	{
		product = null;

		var entry = _entry.Match(line);

		if (!entry.Success || !long.TryParse(entry.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
			return false;

		product = new(
			id,
			entry.Groups["package"].Value,
			entry.Groups["name"].Value,
			entry.Groups["kind"].Value,
			entry.Groups["dir"].Success ? entry.Groups["dir"].Value : string.Empty,
			entry.Groups["updates"].Success ? entry.Groups["updates"].Value.Trim() : string.Empty);

		return true;
	}
}
