namespace StockSharp.Odysseus.Products.Tests;

using System.Globalization;
using System.Linq;

/// <summary>
/// Reading what the installer console printed.
/// </summary>
/// <remarks>
/// The program has no machine-readable output at all, so this is where the whole risk of the product
/// half sits: everything else is a process being started. The samples below are the vendor's own shapes
/// - the console log listener's prefix, and the single ad-hoc line a product is printed as - written out
/// as literals rather than built from the constants under test.
///
/// The contract being pinned is that nothing is guessed at and nothing is dropped. A line that is a
/// product becomes one, the sentences that mean an empty list mean an empty list, and every other line
/// comes back as text - because a listing that quietly loses what it could not read is worse than one
/// that hands it over as prose.
/// </remarks>
[TestClass]
public class InstallerOutputTests : OdysseusTestBase
{
	private const string Prefix = "14:22:33.123 | Application     | ";

	/// <summary>An entry is read into a product, field by field.</summary>
	[TestMethod]
	public void AnEntryIsReadIntoAProduct()
	{
		var reading = InstallerOutput.Read(
			Prefix + "id=9, package_id=StockSharp.Designer: (StandaloneApp) \"Designer\"");

		AreEqual(1, reading.Products.Count, Describe(reading));

		var product = reading.Products[0];

		AreEqual(9L, product.Id);
		AreEqual("StockSharp.Designer", product.PackageId);
		AreEqual("Designer", product.Name);
		AreEqual("StandaloneApp", product.ContentType);
		AreEqual(string.Empty, product.InstalledIn);
		AreEqual(string.Empty, product.Updates);
		IsFalse(product.IsInstalled);

		AreEqual(0, reading.Unparsed.Count, Describe(reading));
	}

	/// <summary>The directory and the available update are read when the line carries them.</summary>
	[TestMethod]
	public void AnInstalledEntryCarriesItsDirectoryAndItsUpdate()
	{
		var reading = InstallerOutput.Read(
			Prefix + @"id=10, package_id=StockSharp.Terminal: (StandaloneApp) ""Terminal"" dir='c:\apps\terminal', updates=5.0.9");

		AreEqual(1, reading.Products.Count, Describe(reading));

		var product = reading.Products[0];

		AreEqual(10L, product.Id);
		AreEqual(@"c:\apps\terminal", product.InstalledIn);
		AreEqual("5.0.9", product.Updates);
		IsTrue(product.IsInstalled);
	}

	/// <summary>
	/// A listing is one log message holding several lines, so only its first line carries the prefix.
	/// A reader that split on the prefix rather than stripping it would find one entry and lose the rest.
	/// </summary>
	[TestMethod]
	public void EveryEntryOfAMultiLineMessageIsRead()
	{
		var reading = InstallerOutput.Read(string.Join(
			Environment.NewLine,
			Prefix + "Products:",
			"id=9, package_id=StockSharp.Designer: (StandaloneApp) \"Designer\"",
			"id=10, package_id=StockSharp.Terminal: (StandaloneApp) \"Terminal\"",
			"id=8, package_id=StockSharp.Hydra: (StandaloneApp) \"Hydra\""));

		AreEqual(3, reading.Products.Count, Describe(reading));

		CollectionAssert.AreEqual(
			new[] { 9L, 10L, 8L },
			reading.Products.Select(p => p.Id).ToArray(),
			"the entries came back in an order other than the one they were printed in.");

		AreEqual(0, reading.Unparsed.Count,
			"the heading of a listing was reported as something a caller has to read: " + Describe(reading));
	}

	/// <summary>
	/// The three sentences that mean an empty list are an empty list rather than an error, and rather
	/// than a line of prose a caller would have to recognise for itself.
	/// </summary>
	[TestMethod]
	public void AnEmptyListingIsAnEmptyListRatherThanText()
	{
		foreach (var sentence in new[]
		{
			"No products found. Try change criteria.",
			"No products found.",
			"No updates found.",
		})
		{
			var reading = InstallerOutput.Read(Prefix + sentence);

			AreEqual(0, reading.Products.Count, sentence);
			AreEqual(0, reading.Unparsed.Count, $"'{sentence}' came back as text: " + Describe(reading));
		}
	}

	/// <summary>Output that is empty, blank or absent reads as nothing at all rather than raising.</summary>
	[TestMethod]
	public void OutputThatSaysNothingReadsAsNothing()
	{
		foreach (var output in new[] { null, string.Empty, "   ", "\r\n\r\n", "\n \n" })
		{
			var reading = InstallerOutput.Read(output);

			AreEqual(0, reading.Products.Count);
			AreEqual(0, reading.Unparsed.Count, Describe(reading));
		}
	}

	/// <summary>
	/// Anything that is not an entry comes back verbatim, with the log prefix taken off. For a listing
	/// this is progress chatter; for a failure it is the whole of the explanation, and the program
	/// writes both down the same pipe.
	/// </summary>
	[TestMethod]
	public void WhatIsNotAnEntryComesBackAsText()
	{
		var reading = InstallerOutput.Read(string.Join(
			Environment.NewLine,
			Prefix + "STATUS: downloading StockSharp.Designer 5.0.9",
			Prefix + "Product 4242 not found.",
			"Press any key to continue"));

		AreEqual(0, reading.Products.Count, Describe(reading));
		AreEqual(3, reading.Unparsed.Count, Describe(reading));

		AreEqual("STATUS: downloading StockSharp.Designer 5.0.9", reading.Unparsed[0],
			"the log prefix was left on the line.");

		AreEqual("Product 4242 not found.", reading.Unparsed[1]);
		AreEqual("Press any key to continue", reading.Unparsed[2]);
	}

	/// <summary>
	/// A line that looks like an entry and is not one is text, not a product with invented fields. The
	/// vendor quotes a product name without escaping it, so a name holding a quote mis-parses - which is
	/// their format and not ours to fix; what is ours is that such a line is handed over rather than
	/// turned into a product called something it is not.
	/// </summary>
	[TestMethod]
	public void SomethingThatOnlyLooksLikeAnEntryIsText()
	{
		foreach (var line in new[]
		{
			"id=nine, package_id=StockSharp.Designer: (StandaloneApp) \"Designer\"",
			"id=11, package_id=StockSharp.Odd: (StandaloneApp) \"A \"quoted\" name\"",
			"id=9 package_id=StockSharp.Designer: (StandaloneApp) \"Designer\"",
			"id=9, package_id=StockSharp.Designer (StandaloneApp) \"Designer\"",
		})
		{
			var reading = InstallerOutput.Read(Prefix + line);

			AreEqual(0, reading.Products.Count, $"'{line}' was read as a product.");
			AreEqual(1, reading.Unparsed.Count, $"'{line}' was dropped instead of being handed back.");
			AreEqual(line, reading.Unparsed[0]);
		}
	}

	/// <summary>
	/// An identifier too large to be a number is a line that looked like an entry and is not one. The
	/// reader hands it back rather than raising: it never throws on what a program happened to print.
	/// </summary>
	[TestMethod]
	public void AnIdentifierTooLargeToBeANumberIsText()
	{
		var line = "id=999999999999999999999999, package_id=StockSharp.Huge: (StandaloneApp) \"Huge\"";

		var reading = InstallerOutput.Read(Prefix + line);

		AreEqual(0, reading.Products.Count);
		AreEqual(1, reading.Unparsed.Count, Describe(reading));
		AreEqual(line, reading.Unparsed[0]);
	}

	/// <summary>A product name that is empty is a name, not a reason to refuse the line.</summary>
	[TestMethod]
	public void AnEmptyNameIsStillAnEntry()
	{
		var reading = InstallerOutput.Read(Prefix + "id=7, package_id=StockSharp.Nameless: () \"\"");

		AreEqual(1, reading.Products.Count, Describe(reading));
		AreEqual(string.Empty, reading.Products[0].Name);
		AreEqual(string.Empty, reading.Products[0].ContentType);
	}

	/// <summary>
	/// The hardware identifier is written as markup rather than as a line, so it arrives with no newline
	/// after it and has to be taken out of whatever else was printed.
	/// </summary>
	[TestMethod]
	public void TheHardwareIdIsTakenOutOfTheLineItWasWrittenOn()
	{
		AreEqual("A1B2C3D4", InstallerOutput.HardwareId("Hardware ID: A1B2C3D4"));
		AreEqual("A1B2C3D4", InstallerOutput.HardwareId("Hardware ID: A1B2C3D4\r\n"));
		AreEqual("A1B2C3D4", InstallerOutput.HardwareId(Prefix + "Hardware ID: A1B2C3D4"));

		// Escape sequences, in case the program ever decides it is talking to a terminal.
		AreEqual("A1B2C3D4", InstallerOutput.HardwareId("Hardware ID: \u001b[38;5;208mA1B2C3D4\u001b[0m"));
	}

	/// <summary>
	/// Output that carries no identifier gives an empty one rather than a guess. The command fails the
	/// way everything else does - by logging an exception and exiting - and a log line is not an id.
	/// </summary>
	[TestMethod]
	public void OutputWithNoHardwareIdGivesNone()
	{
		AreEqual(string.Empty, InstallerOutput.HardwareId(null));
		AreEqual(string.Empty, InstallerOutput.HardwareId(string.Empty));
		AreEqual(string.Empty, InstallerOutput.HardwareId(Prefix + "System.InvalidOperationException: no."));
		AreEqual(string.Empty, InstallerOutput.HardwareId("Hardware ID:    "),
			"a label with nothing after it was read as an identifier.");
	}

	/// <summary>
	/// The refusal that means another installer holds the machine is recognised, because it is the one
	/// failure with an obvious remedy and it arrives as prose like everything else.
	/// </summary>
	[TestMethod]
	public void TheForeignInstallerRefusalIsRecognised()
	{
		IsTrue(InstallerOutput.NamesAForeignInstaller(
			"Application is already running. Following processes must be closed: StockSharp.Installer.UI.exe (id=42)"));

		IsTrue(InstallerOutput.NamesAForeignInstaller(Prefix + "installer is already running. trying to stop..."));

		IsFalse(InstallerOutput.NamesAForeignInstaller("Product 4242 not found."));
		IsFalse(InstallerOutput.NamesAForeignInstaller(null));
		IsFalse(InstallerOutput.NamesAForeignInstaller(string.Empty));
	}

	/// <summary>
	/// An answer carries the end of what was printed, because a failure explains itself in its last
	/// lines and an answer must not become a hundred thousand of them.
	/// </summary>
	[TestMethod]
	public void AnAnswerCarriesTheEndOfWhatWasPrinted()
	{
		var lines = Enumerable.Range(1, 100).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();

		var tail = InstallerOutput.Tail(lines, 40);

		AreEqual(40, tail.Count);
		AreEqual("61", tail[0]);
		AreEqual("100", tail[^1]);

		AreEqual(3, InstallerOutput.Tail(new[] { "a", "b", "c" }, 40).Count, "a short list was cut.");
		AreEqual(0, InstallerOutput.Tail(null, 40).Count);
		AreEqual(0, InstallerOutput.Tail(lines, 0).Count);
	}

	private static string Describe(InstallerReading reading)
		=> $"products: [{string.Join(" | ", reading.Products.Select(p => p.Name))}], " +
			$"unparsed: [{string.Join(" | ", reading.Unparsed)}]";
}
