namespace Odysseus.InstallerStub;

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// An installer console that misbehaves on purpose.
/// </summary>
/// <remarks>
/// The interesting outcomes of driving the real installer cannot be produced on demand by the real
/// installer. It needs a StockSharp account, a network round trip on every call, a machine nobody else
/// is installing on, and twenty minutes to fail an install - and three of the things worth testing are
/// a program that hangs, a program that waits for a keypress, and a program that refuses because
/// another copy of it holds the machine.
///
/// So this is a console in every respect the driver cares about: it takes the same command line, prints
/// the same log shape, and does one wrong thing per run, named by the environment rather than by an
/// argument - because the arguments are the driver's to build, and this echoes them back so a test can
/// read exactly what it was given.
///
/// Everything it prints is copied from the vendor's own source: the console log listener's
/// <c>HH:mm:ss.fff | source | message</c>, and the one ad-hoc line a product is printed as. Nothing here
/// is written against this repository's constants for those, so a drift between the two shows up as a
/// failing test rather than as two files agreeing with each other.
/// </remarks>
public static class Program
{
	/// <summary>Environment variable naming the one wrong thing this run is to do.</summary>
	public const string BehaviourVariable = "ODYSSEUS_INSTALLER_STUB";

	/// <summary>Prefix each echoed argument is printed under, in the order it was received.</summary>
	public const string ArgumentPrefix = "arg: ";

	/// <summary>Prefix the working directory is printed under.</summary>
	public const string DirectoryPrefix = "cwd: ";

	/// <summary>Prefix what a read of standard input came to is printed under.</summary>
	public const string ReadPrefix = "read: ";

	/// <summary>The hardware identifier this stub always reports.</summary>
	public const string HardwareId = "STUB-HW-0123456789";

	/// <summary>
	/// Behaves badly in the one way it was asked to.
	/// </summary>
	/// <param name="args">The command line the driver built.</param>
	/// <returns>Process exit code.</returns>
	public static async Task<int> Main(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		var behaviour = Environment.GetEnvironmentVariable(BehaviourVariable);

		if (string.IsNullOrWhiteSpace(behaviour))
			behaviour = "echo";

		// A hang says nothing and answers nothing: it is the deadline that has to end it.
		if (behaviour == "hang")
		{
			await Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None);

			return 0;
		}

		Echo(args);

		switch (behaviour)
		{
			case "products":
				Listing(installed: false);

				return 0;

			case "installed":
				Listing(installed: true);

				return 0;

			case "empty":
				Line("No products found. Try change criteria.");

				return 0;

			case "hddid":
				// Written as markup rather than as a line, so there is no newline after it.
				Console.Out.Write("Hardware ID: " + HardwareId);
				Console.Out.Flush();

				return 0;

			case "garbage":
				Garbage();

				return 0;

			case "fail":
				Line("Product 4242 not found.");
				WaitOnError();

				return -2;

			case "busy":
				// The refusal the real one raises when another installer holds the machine, in the same
				// hard-coded English it uses.
				Line("Application is already running. Following processes must be closed: StockSharp.Installer.UI.exe (id=4242)");
				WaitOnError();

				return -2;

			case "read":
				// Proves the driver closed standard input. Left open, this waits for a keypress that is
				// never coming and the run ends on its deadline instead.
				Console.Out.WriteLine(ReadPrefix + Console.In.Read().ToString(CultureInfo.InvariantCulture));

				return 0;

			default:
				return 0;
		}
	}

	/// <summary>
	/// Prints the command line and the directory it was started in, one thing per line.
	/// </summary>
	/// <param name="args">The command line the driver built.</param>
	private static void Echo(string[] args)
	{
		Console.Out.WriteLine(DirectoryPrefix + Directory.GetCurrentDirectory());

		foreach (var argument in args)
			Console.Out.WriteLine(ArgumentPrefix + argument);
	}

	/// <summary>
	/// Prints a product listing exactly as the console prints one.
	/// </summary>
	/// <param name="installed">Whether the products are to look installed.</param>
	/// <remarks>
	/// One log message holding several lines, so only the first of them carries the log prefix. That is
	/// the shape the reader has to survive, and it is the shape a listing actually has. The update on
	/// the second entry is what the updates listing adds; it is here because the reader has to read it
	/// wherever it turns up.
	/// </remarks>
	private static void Listing(bool installed)
	{
		var designer = "id=9, package_id=StockSharp.Designer: (StandaloneApp) \"Designer\"";
		var terminal = "id=10, package_id=StockSharp.Terminal: (StandaloneApp) \"Terminal\"";

		if (installed)
		{
			designer += @" dir='c:\apps\designer'";
			terminal += @" dir='c:\apps\terminal', updates=5.0.9";
		}

		Line("Products:" + Environment.NewLine + designer + Environment.NewLine + terminal);
	}

	/// <summary>
	/// Prints output nothing can be read out of, in the shapes that actually happen.
	/// </summary>
	private static void Garbage()
	{
		// A line that starts like an entry and is not one.
		Line("id=nine, package_id=StockSharp.Designer: (StandaloneApp) \"Designer\"");

		// An entry whose name carries the quote its own format has no escape for.
		Line("id=11, package_id=StockSharp.Odd: (StandaloneApp) \"A \"quoted\" name\"");

		// An identifier too large to be a number.
		Line("id=999999999999999999999999, package_id=StockSharp.Huge: (StandaloneApp) \"Huge\"");

		// A progress report, which is an ordinary log line and belongs in the unparsed output.
		Line("STATUS: downloading StockSharp.Designer 5.0.9");

		// Blank output, which must not become entries of its own.
		Console.Out.WriteLine();
		Console.Out.WriteLine("   ");
	}

	/// <summary>
	/// Prints one line the way the console's log listener does.
	/// </summary>
	/// <param name="message">Message to print, which may hold newlines.</param>
	private static void Line(string message)
		=> Console.Out.WriteLine(
			DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) +
			" | " + "Application".PadRight(15) + " | " + message);

	/// <summary>
	/// Waits for a keypress before returning from a failure, as every failure path of the real one does.
	/// </summary>
	private static void WaitOnError()
	{
		Console.Out.WriteLine("Press any key to continue");
		Console.In.Read();
	}
}
