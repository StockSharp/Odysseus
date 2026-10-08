namespace StockSharp.Odysseus.Products.Tests;

using System.IO;

/// <summary>
/// Where the installer is looked for, and what it is allowed to touch.
/// </summary>
/// <remarks>
/// Read from the real environment rather than from an injected table, for the reason the server's own
/// options tests give: an operator sets a variable and starts the process, and a test that handed the
/// reader its own dictionary would prove nothing about that. The variable name is written out as a
/// literal so that renaming it is a failure rather than a silent agreement.
/// </remarks>
[TestClass]
public class ProductInstallerOptionsTests : OdysseusTestBase
{
	private const string PathVariable = "ODYSSEUS_INSTALLER";

	private string _saved;

	/// <summary>Puts the environment aside, so each test reads one this test wrote.</summary>
	[TestInitialize]
	public void ClearTheEnvironment()
	{
		_saved = Environment.GetEnvironmentVariable(PathVariable);

		Environment.SetEnvironmentVariable(PathVariable, null);
	}

	/// <summary>Gives the environment back, whatever the test did to it.</summary>
	[TestCleanup]
	public void RestoreTheEnvironment()
		=> Environment.SetEnvironmentVariable(PathVariable, _saved);

	/// <summary>
	/// With nothing said, the console is looked for in one place: a folder of its own beside the host,
	/// which is where a deployment that ships it would put it.
	/// </summary>
	[TestMethod]
	public void AnEnvironmentThatSaysNothingLooksBesideTheHost()
	{
		var candidates = ProductInstallerOptions.Candidates();

		AreEqual(1, candidates.Count, Join(candidates));

		var beside = candidates[0];

		IsTrue(Path.IsPathRooted(beside), $"the place looked in is not an absolute path: {beside}");
		AreEqual("installer", Path.GetFileName(Path.GetDirectoryName(beside)));

		// The whole name is compared, because the name has dots of its own: where the platform adds no
		// extension, taking one off takes off a part of the name.
		AreEqual(
			OperatingSystem.IsWindows() ? "StockSharp.Installer.Console.exe" : "StockSharp.Installer.Console",
			Path.GetFileName(beside));
	}

	/// <summary>
	/// The variable comes first and the folder beside the host stays in the list, so a report of where
	/// the console was looked for names both rather than only the one that was tried last.
	/// </summary>
	[TestMethod]
	public void TheVariableComesFirstAndTheOtherPlaceIsStillReported()
	{
		Environment.SetEnvironmentVariable(PathVariable, Path.Combine("tools", "installer.exe"));

		var candidates = ProductInstallerOptions.Candidates();

		AreEqual(2, candidates.Count, Join(candidates));

		IsTrue(Path.IsPathRooted(candidates[0]), $"a relative path was kept relative: {candidates[0]}");
		AreEqual("installer.exe", Path.GetFileName(candidates[0]));
		AreEqual("installer", Path.GetFileName(Path.GetDirectoryName(candidates[1])));
	}

	/// <summary>
	/// Being unable to find the console is an answer rather than a failure, and it is the ordinary case:
	/// almost no machine has this program on it.
	/// </summary>
	[TestMethod]
	public void AConsoleThatIsNotThereIsAnEmptyPathRatherThanAThrow()
	{
		var missing = Path.Combine(Path.GetTempPath(), $"odysseus-installer-{Guid.NewGuid():n}.exe");

		Environment.SetEnvironmentVariable(PathVariable, missing);

		AreEqual(string.Empty, Options().Locate());
	}

	/// <summary>The console that exists is the one used, and it is looked for again on every call.</summary>
	[TestMethod]
	public void TheConsoleIsLookedForAgainEachTime()
	{
		var folder = Directory.CreateTempSubdirectory("odysseus-installer").FullName;

		try
		{
			var console = Path.Combine(folder, "StockSharp.Installer.Console.exe");

			Environment.SetEnvironmentVariable(PathVariable, console);

			var options = Options();

			AreEqual(string.Empty, options.Locate(), "a console that is not there was located.");

			File.WriteAllText(console, "not a program, and nothing here runs it");

			AreEqual(console, options.Locate(),
				"a console installed while the server ran was not picked up; an operator would have to " +
				"restart for it, which is a worse product.");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	/// <summary>
	/// Products land under the projects root, in a folder of their own, and every invocation's captured
	/// output goes under that.
	/// </summary>
	[TestMethod]
	public void EverythingLandsUnderTheProjectsRoot()
	{
		var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "odysseus-projects"));

		var options = ProductInstallerOptions.Read(root, [9]);

		AreEqual(Path.Combine(root, "products"), options.InstallRoot);
		AreEqual(Path.Combine(root, "products", "invocations"), options.Invocations);
		AreEqual(Path.Combine(root, "products", "9"), options.DirectoryFor(9));
	}

	/// <summary>
	/// A directory that leaves the install root is refused. Nothing an agent sends reaches this today -
	/// the folder is derived from the product identifier - and the check is here so that stays true.
	/// </summary>
	[TestMethod]
	public void ADirectoryOutsideTheInstallRootIsRefused()
	{
		var options = ProductInstallerOptions.Read(Path.Combine(Path.GetTempPath(), "odysseus-projects"), [9]);

		foreach (var name in new[]
		{
			"..",
			Path.Combine("..", "elsewhere"),
			Path.Combine("..", "..", "windows", "system32"),
			Path.Combine("9", "..", "..", "escaped"),
			OperatingSystem.IsWindows() ? @"C:\windows\system32" : "/etc",
			".",
		})
		{
			Throws<ArgumentException>(
				() => options.DirectoryFor(name),
				$"'{name}' was accepted as a place to install a product into.");
		}

		Throws<ArgumentNullException>(() => options.DirectoryFor(null));
	}

	/// <summary>A folder under the install root is accepted, including one nested inside it.</summary>
	[TestMethod]
	public void ADirectoryInsideTheInstallRootIsAccepted()
	{
		var options = ProductInstallerOptions.Read(Path.Combine(Path.GetTempPath(), "odysseus-projects"), [9]);

		var nested = options.DirectoryFor(Path.Combine("9", "plugins"));

		IsTrue(nested.StartsWith(options.InstallRoot, StringComparison.Ordinal), nested);
	}

	/// <summary>
	/// Nothing may be installed unless the operator named it. The list is sorted, has its duplicates
	/// dropped and refuses everything not on it, so a report of what is allowed reads the same way twice.
	/// </summary>
	[TestMethod]
	public void OnlyWhatTheOperatorNamedIsAllowed()
	{
		var options = ProductInstallerOptions.Read(Path.GetTempPath(), [1137, 9, 9]);

		AreEqual(2, options.AllowedProducts.Count, string.Join(", ", options.AllowedProducts));
		AreEqual(9L, options.AllowedProducts[0]);
		AreEqual(1137L, options.AllowedProducts[1]);

		IsTrue(options.Allows(9));
		IsTrue(options.Allows(1137));
		IsFalse(options.Allows(10));

		IsFalse(ProductInstallerOptions.Read(Path.GetTempPath(), []).Allows(9),
			"a server that was given no product identifiers allowed one.");
	}

	/// <summary>
	/// The deadlines are properties of the machine rather than of a request, and a read is not given an
	/// install's twenty minutes: every listing forces a full catalogue reload over the network, and there
	/// is no offline mode, but that is a round trip and not a download.
	/// </summary>
	[TestMethod]
	public void EachVerbIsGivenTheTimeItsWorkTakes()
	{
		var options = ProductInstallerOptions.Read(Path.GetTempPath(), [9]);

		AreEqual(TimeSpan.FromSeconds(90), options.DeadlineFor(InstallerVerbs.Products));
		AreEqual(TimeSpan.FromSeconds(90), options.DeadlineFor(InstallerVerbs.Installed));
		AreEqual(TimeSpan.FromSeconds(90), options.DeadlineFor(InstallerVerbs.HddId));
		AreEqual(TimeSpan.FromMinutes(20), options.DeadlineFor(InstallerVerbs.Install));
		AreEqual(TimeSpan.FromMinutes(20), options.DeadlineFor(InstallerVerbs.Update));
		AreEqual(TimeSpan.FromMinutes(5), options.DeadlineFor(InstallerVerbs.Remove));
	}

	/// <summary>
	/// The account is the installer's own and is not somewhere this server chooses: it lies in the
	/// documents folder of whoever runs the console. Reported as the real path so that whoever has to
	/// go and sign in knows where the installer will look.
	/// </summary>
	[TestMethod]
	public void TheAccountFileIsTheOneTheInstallerActuallyReads()
	{
		var documents = Path.Combine(Path.GetTempPath(), "somebody", "Documents");
		var installRoot = Path.Combine(Path.GetTempPath(), "projects", "products");

		AreEqual(
			Path.Combine(documents, "StockSharp", "credentials.json"),
			ProductInstallerOptions.AccountFileOf(documents, installRoot));
	}

	/// <summary>
	/// Where the platform names no documents folder, the installer is left with a bare name and settles
	/// it against the directory it was started in, which is the install root. The account is reported
	/// there, and not against the directory this server happened to start in, where the console never
	/// looks.
	/// </summary>
	[TestMethod]
	public void WithNoDocumentsFolderTheAccountIsWhereTheConsoleIsStarted()
	{
		var installRoot = Path.Combine(Path.GetTempPath(), "projects", "products");

		AreEqual(
			Path.Combine(installRoot, "StockSharp", "credentials.json"),
			ProductInstallerOptions.AccountFileOf(string.Empty, installRoot));
	}

	/// <summary>The options a server starts with name that file, whatever machine they are read on.</summary>
	[TestMethod]
	public void TheOptionsAServerStartsWithNameTheAccountFile()
	{
		var account = ProductInstallerOptions.Read(Path.GetTempPath(), [9]).AccountFile;

		IsTrue(Path.IsPathRooted(account), $"the account file is not an absolute path: {account}");
		AreEqual("credentials.json", Path.GetFileName(account));
		AreEqual("StockSharp", Path.GetFileName(Path.GetDirectoryName(account)));
	}

	/// <summary>An account nobody signed in is absent, and saying so is the whole of the check.</summary>
	[TestMethod]
	public void AnAccountThatIsNotThereIsReportedAsAbsent()
	{
		var absent = Options() with { AccountFile = Path.Combine(Path.GetTempPath(), $"odysseus-{Guid.NewGuid():n}.json") };

		IsFalse(absent.HasAccount);

		var file = Path.Combine(Directory.CreateTempSubdirectory("odysseus-account").FullName, "credentials.json");

		try
		{
			File.WriteAllText(file, "{}");

			IsTrue((Options() with { AccountFile = file }).HasAccount);
		}
		finally
		{
			Directory.Delete(Path.GetDirectoryName(file), recursive: true);
		}
	}

	private static ProductInstallerOptions Options()
		=> ProductInstallerOptions.Read(Path.Combine(Path.GetTempPath(), "odysseus-projects"), [9]);

	private static string Join(IEnumerable<string> values)
		=> "looked in: " + string.Join(", ", values);
}
