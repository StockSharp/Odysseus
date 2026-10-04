namespace Odysseus.Products.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Odysseus.Application;
using Odysseus.Products;
using Odysseus.TestKit;

/// <summary>
/// What happens when the program this server drives is not there, or is there and misbehaves.
/// </summary>
/// <remarks>
/// None of this needs the real installer, and it could not use it if it wanted to: the real one needs a
/// StockSharp account, a network round trip on every call, and a machine nobody else is installing on,
/// and three of the outcomes worth pinning down - a hang, a wait for a keypress, and a refusal because
/// another copy holds the machine - cannot be asked of it on demand. So these run against the stub next
/// door, which is the same arrangement the worker tests use and for the same stated reason.
///
/// The one thing every machine will actually meet is the first test: the console is simply not there.
/// That is the ordinary case rather than a fault, and it has to be an answer rather than a crash.
/// </remarks>
[TestClass]
public class ConsoleProductInstallerTests : OdysseusTestBase
{
	/// <summary>Environment variable the stub takes its one wrong behaviour from.</summary>
	private const string BehaviourVariable = "ODYSSEUS_INSTALLER_STUB";

	/// <summary>Prefix the stub echoes each argument under.</summary>
	private const string ArgumentPrefix = "arg: ";

	/// <summary>Prefix the stub echoes its working directory under.</summary>
	private const string DirectoryPrefix = "cwd: ";

	/// <summary>The identifier the stub reports for this machine.</summary>
	private const string StubHardwareId = "STUB-HW-0123456789";

	private string _root;
	private string _account;
	private string _saved;

	/// <summary>Gives the test a projects root of its own and an account that is signed in.</summary>
	[TestInitialize]
	public void Prepare()
	{
		_saved = Environment.GetEnvironmentVariable(BehaviourVariable);

		// Cleared as well as saved, so a test that does not name a behaviour gets the plain one rather
		// than whatever the machine happened to have set.
		Environment.SetEnvironmentVariable(BehaviourVariable, null);

		_root = Path.Combine(Path.GetTempPath(), "odysseus-products", Guid.NewGuid().ToString("n"));

		Directory.CreateDirectory(_root);

		_account = Path.Combine(_root, "credentials.json");

		File.WriteAllText(_account, "{}");
	}

	/// <summary>Takes the environment and the directory back.</summary>
	[TestCleanup]
	public void CleanUp()
	{
		Environment.SetEnvironmentVariable(BehaviourVariable, _saved);

		if (_root is not null && Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>
	/// The ordinary case, and the whole requirement: a machine with no installer on it gets an answer
	/// saying what is missing and what still works, not a failure raised while a program that is not
	/// there was being started.
	/// </summary>
	[TestMethod]
	public async Task AConsoleThatIsNotThereIsAnAnswerRatherThanAFailure()
	{
		using var installer = new ConsoleProductInstaller(Options(Missing()));

		var state = await installer.DescribeAsync(CancellationToken);

		IsFalse(state.IsAvailable);
		AreEqual(ProductInstallerState.NoConsole, state.BlockedBy);
		AreEqual(string.Empty, state.ConsolePath, "a console was reported at a path that holds nothing.");

		IsTrue(state.LookedIn.Count > 0,
			"the report does not say where the console was looked for, which is the one thing an operator " +
			"needs in order to put it there.");

		AreEqual(_account, state.AccountFile);
		AreEqual(Path.Combine(_root, "products"), state.InstallRoot);
		AreEqual(string.Empty, state.HardwareId);
	}

	/// <summary>
	/// The other five methods refuse before starting anything, naming the variable and the places that
	/// were tried. The refusal is the broker one with the nouns changed: this server never obtains the
	/// program, and everything that is not a product works without it.
	/// </summary>
	[TestMethod]
	public async Task AConsoleThatIsNotThereRefusesEveryProductCall()
	{
		var missing = Missing();

		using var installer = new ConsoleProductInstaller(Options(missing));

		foreach (var call in Calls(installer))
		{
			var refusal = await ThrowsAsync<ProductInstallerUnavailableException>(call);

			IsTrue(refusal.Message.Contains(ProductInstallerOptions.PathVariable, StringComparison.Ordinal),
				$"the refusal does not name the variable that would fix it: {refusal.Message}");

			IsTrue(refusal.Message.Contains(missing, StringComparison.Ordinal),
				$"the refusal does not say where the console was looked for: {refusal.Message}");
		}
	}

	/// <summary>
	/// The command line the console is actually given, and the directory it is started in - which is the
	/// whole of the confinement, because the console resolves everything it writes against it.
	/// </summary>
	[TestMethod]
	public async Task TheCommandLineAndTheWorkingDirectoryAreTheOnesIntended()
	{
		using var installer = Driver("echo");

		var outcome = await installer.ListAsync("designer", CancellationToken);

		IsTrue(outcome.Succeeded, Describe(outcome));

		CollectionAssert.AreEqual(
			new[] { "Products", "--search", "designer" },
			Arguments(outcome),
			"the console was given a command line other than the intended one: " + Describe(outcome));

		AreEqual(
			Path.GetFullPath(Path.Combine(_root, "products")),
			Path.GetFullPath(Line(outcome, DirectoryPrefix)),
			"the console was started somewhere other than the install root, which is where it resolves " +
			"both its install directory and its own log folder against.");
	}

	/// <summary>
	/// An install names a directory under the projects root, derived from the identifier. A caller never
	/// names one: a tool argument that is a filesystem path is a tool argument that writes anywhere.
	/// </summary>
	[TestMethod]
	public async Task AnInstallIsGivenADirectoryUnderTheProjectsRoot()
	{
		using var installer = Driver("echo");

		var outcome = await installer.InstallAsync(9, reinstall: true, CancellationToken);

		var arguments = Arguments(outcome);

		CollectionAssert.AreEqual(
			new[] { "Install", "9", Path.Combine(_root, "products", "9"), "--noerror" },
			arguments,
			"the install was not confined to the projects root: " + Describe(outcome));
	}

	/// <summary>A listing comes back as products, with the identifiers a later call is made by.</summary>
	[TestMethod]
	public async Task AListingIsReadIntoProducts()
	{
		using var installer = Driver("products");

		var outcome = await installer.ListAsync(string.Empty, CancellationToken);

		IsTrue(outcome.Succeeded, Describe(outcome));
		AreEqual(2, outcome.Products.Count, Describe(outcome));

		AreEqual(9L, outcome.Products[0].Id);
		AreEqual("Designer", outcome.Products[0].Name);
		AreEqual("StockSharp.Designer", outcome.Products[0].PackageId);
		IsFalse(outcome.Products[0].IsInstalled);

		AreEqual(10L, outcome.Products[1].Id);
	}

	/// <summary>An installed product carries the directory it is in and any update waiting for it.</summary>
	[TestMethod]
	public async Task AnInstalledListingCarriesDirectoriesAndUpdates()
	{
		using var installer = Driver("installed");

		var outcome = await installer.InstalledAsync(string.Empty, CancellationToken);

		AreEqual(2, outcome.Products.Count, Describe(outcome));

		IsTrue(outcome.Products[0].IsInstalled);
		IsTrue(outcome.Products[0].InstalledIn.EndsWith("designer", StringComparison.OrdinalIgnoreCase),
			outcome.Products[0].InstalledIn);

		AreEqual("5.0.9", outcome.Products[1].Updates);

		CollectionAssert.AreEqual(new[] { "Installed" }, Arguments(outcome), Describe(outcome));
	}

	/// <summary>
	/// Nothing matching is an empty list rather than a failure, and the sentence that says so is not
	/// handed back as something a caller has to recognise for itself.
	/// </summary>
	[TestMethod]
	public async Task AnEmptyListingIsNotAFailure()
	{
		using var installer = Driver("empty");

		var outcome = await installer.ListAsync("nothing like this", CancellationToken);

		IsTrue(outcome.Succeeded, Describe(outcome));
		AreEqual(0, outcome.Products.Count, Describe(outcome));

		IsFalse(outcome.Unparsed.Any(line => line.Contains("No products found", StringComparison.Ordinal)),
			"the sentence that means an empty list came back as prose: " + Describe(outcome));
	}

	/// <summary>
	/// What the reader could not understand comes back as text. Dropping it would be a shorter answer
	/// and a dishonest one - the program has no machine-readable output, so the lines this cannot read
	/// are exactly the ones a person has to.
	/// </summary>
	[TestMethod]
	public async Task OutputThatCannotBeReadComesBackAsText()
	{
		using var installer = Driver("garbage");

		var outcome = await installer.ListAsync(string.Empty, CancellationToken);

		AreEqual(0, outcome.Products.Count,
			"a line that only looked like an entry was read as a product: " + Describe(outcome));

		IsTrue(outcome.Unparsed.Any(line => line.Contains("id=nine", StringComparison.Ordinal)), Describe(outcome));
		IsTrue(outcome.Unparsed.Any(line => line.Contains("STATUS:", StringComparison.Ordinal)), Describe(outcome));

		IsFalse(outcome.Unparsed.Any(string.IsNullOrWhiteSpace),
			"blank output was handed back as lines to read: " + Describe(outcome));
	}

	/// <summary>
	/// A failure is an outcome carrying what the program said, not a refusal. The console spends almost
	/// every failure through one exit code, so the code says nothing and the output is the explanation -
	/// which means the answer has to carry it.
	/// </summary>
	[TestMethod]
	public async Task AFailureIsAnOutcomeCarryingWhatTheProgramSaid()
	{
		using var installer = Driver("fail");

		var outcome = await installer.InstallAsync(9, reinstall: false, CancellationToken);

		IsFalse(outcome.Succeeded);

		// Non-zero and nothing finer. The console spends almost every failure through one code, and what
		// that code is depends on the platform anyway: a negative status is masked to a byte on Unix.
		// It is a diagnostic in the answer and it is in no branch.
		AreNotEqual(0, outcome.ExitCode);

		IsTrue(outcome.Unparsed.Any(line => line.Contains("Product 4242 not found", StringComparison.Ordinal)),
			"the program's own account of the failure did not reach the caller: " + Describe(outcome));
	}

	/// <summary>
	/// Every failure path of the console reads a key before it returns, and the switch named after
	/// suppressing errors does not turn that off. Standard input is closed the moment it starts, so that
	/// read ends at once instead of waiting for a keypress nothing here can give it.
	/// </summary>
	[TestMethod]
	public async Task StandardInputIsClosedSoAWaitForAKeypressEndsAtOnce()
	{
		using var installer = Driver("read");

		var outcome = await installer.ListAsync(string.Empty, CancellationToken);

		IsTrue(outcome.Succeeded, Describe(outcome));

		// What a read of a closed pipe comes to. Left open, the program would still be waiting.
		AreEqual("-1", Line(outcome, "read: "),
			"standard input was left open, so a program waiting for a keypress would wait for ever: " +
			Describe(outcome));
	}

	/// <summary>
	/// The console takes no cancellation of any kind, so the only way to end one is to kill it. A run
	/// that overruns says how long it was given and where what it printed was kept.
	/// </summary>
	[TestMethod]
	public async Task AnInvocationThatOverrunsIsKilledAndSaysWhere()
	{
		var options = Options(Stub()) with
		{
			ReadDeadline = TimeSpan.FromSeconds(2),
		};

		Environment.SetEnvironmentVariable(BehaviourVariable, "hang");

		using var installer = new ConsoleProductInstaller(options);

		var stopped = await ThrowsAsync<ProductInstallerTimedOutException>(
			() => installer.ListAsync(string.Empty, CancellationToken).AsTask());

		IsTrue(stopped.Message.Contains("2 seconds", StringComparison.Ordinal),
			$"the refusal does not say how long it was given: {stopped.Message}");

		IsTrue(Directory.EnumerateFiles(Path.Combine(_root, "products", "invocations")).Any(),
			"nothing was written down about a run that was killed, so there is nothing to read afterwards.");
	}

	/// <summary>
	/// The machine-wide refusal recognised in the output, which is where it arrives when another
	/// installer takes the machine between the check and the start.
	/// </summary>
	[TestMethod]
	public async Task AForeignInstallerInTheOutputIsAConflict()
	{
		using var installer = Driver("busy");

		var conflict = await ThrowsAsync<ProductInstallerBusyException>(
			() => installer.InstallAsync(9, reinstall: false, CancellationToken).AsTask());

		IsTrue(conflict.Message.Contains("machine-wide", StringComparison.Ordinal), conflict.Message);
	}

	/// <summary>
	/// A foreign installer already on the machine is refused before anything is started, so the answer
	/// names the conflict in a category to branch on rather than leaving it to be read out of prose -
	/// and so that nothing here asks somebody's open installer window to close, which is what the
	/// vendor's own version of this check does.
	/// </summary>
	[TestMethod]
	public async Task AForeignInstallerOnTheMachineIsRefusedBeforeAnythingIsStarted()
	{
		using var self = Process.GetCurrentProcess();

		// The one installer this machine certainly has running is the test itself, so it stands in for a
		// foreign one. Nothing else could be relied on to be there.
		var options = Options(Stub()) with { ForeignProcesses = [self.ProcessName] };

		using var installer = new ConsoleProductInstaller(options);

		var conflict = await ThrowsAsync<ProductInstallerBusyException>(
			() => installer.ListAsync(string.Empty, CancellationToken).AsTask());

		IsTrue(conflict.Message.Contains("already running", StringComparison.Ordinal), conflict.Message);

		IsFalse(Directory.Exists(Path.Combine(_root, "products", "invocations")),
			"a refusal made before anything starts started something.");
	}

	/// <summary>
	/// The hardware identifier needs neither an account nor the network, so it is read even by a machine
	/// that cannot install anything - which is the machine whose operator needs it, to go and buy a
	/// licence with.
	/// </summary>
	[TestMethod]
	public async Task TheHardwareIdIsReadEvenWhenNothingCanBeInstalled()
	{
		var options = Options(Stub()) with { AccountFile = Path.Combine(_root, "not-signed-in.json") };

		Environment.SetEnvironmentVariable(BehaviourVariable, "hddid");

		using var installer = new ConsoleProductInstaller(options);

		var state = await installer.DescribeAsync(CancellationToken);

		AreEqual(StubHardwareId, state.HardwareId);
		IsFalse(state.IsAvailable);
		AreEqual(ProductInstallerState.NoAccount, state.BlockedBy);
		AreEqual(Stub(), state.ConsolePath);
	}

	/// <summary>
	/// A product the operator did not name is refused before anything is started, and the refusal says
	/// which identifiers were allowed - so the caller can choose again instead of guessing.
	/// </summary>
	[TestMethod]
	public async Task AProductTheOperatorDidNotAllowIsRefusedBeforeAnythingIsStarted()
	{
		var options = Options(Stub()) with { AllowedProducts = [9] };

		using var installer = new ConsoleProductInstaller(options);

		foreach (var call in new Func<Task>[]
		{
			() => installer.InstallAsync(1137, reinstall: false, CancellationToken).AsTask(),
			() => installer.UpdateAsync(1137, backupSettings: false, CancellationToken).AsTask(),
			() => installer.RemoveAsync(1137, removeData: false, CancellationToken).AsTask(),
		})
		{
			var refusal = await ThrowsAsync<ProductInstallerUnavailableException>(call);

			IsTrue(refusal.Message.Contains("1137", StringComparison.Ordinal), refusal.Message);

			IsTrue(refusal.Message.Contains("allowed 9", StringComparison.Ordinal),
				$"the refusal does not say what may be installed instead: {refusal.Message}");
		}

		IsFalse(Directory.Exists(Path.Combine(_root, "products", "invocations")),
			"a product outside the allow-list started the installer anyway.");
	}

	/// <summary>
	/// An operator who named no product identifiers turned the whole surface off, listings included.
	/// They take the same machine-wide mutex, need the same account and make the same network round
	/// trip as an install, and an instance that may install nothing has nothing useful to say about what
	/// it might install.
	/// </summary>
	[TestMethod]
	public async Task AServerThatMayInstallNothingRefusesTheListingsToo()
	{
		var options = Options(Stub()) with { AllowedProducts = [] };

		using var installer = new ConsoleProductInstaller(options);

		var refusal = await ThrowsAsync<ProductInstallerUnavailableException>(
			() => installer.ListAsync(string.Empty, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("no product at all", StringComparison.Ordinal), refusal.Message);

		var state = await installer.DescribeAsync(CancellationToken);

		IsFalse(state.IsAvailable);
		AreEqual(ProductInstallerState.NoneAllowed, state.BlockedBy);
		AreEqual(0, state.AllowedProducts.Count);
	}

	/// <summary>
	/// Without an account the console stops and waits for an email address to be typed at a keyboard.
	/// Nothing here can answer that, so the call is refused now rather than left to run into its
	/// deadline - which is the difference between a refusal a caller can act on and a wait it cannot.
	/// </summary>
	[TestMethod]
	public async Task WithoutAnAccountEveryProductCallIsRefusedRatherThanLeftToHang()
	{
		var absent = Path.Combine(_root, "not-signed-in.json");

		var options = Options(Stub()) with { AccountFile = absent };

		using var installer = new ConsoleProductInstaller(options);

		foreach (var call in Calls(installer))
		{
			var refusal = await ThrowsAsync<ProductInstallerUnavailableException>(call);

			IsTrue(refusal.Message.Contains(absent, StringComparison.Ordinal),
				$"the refusal does not name the file that has to exist: {refusal.Message}");
		}
	}

	/// <summary>
	/// Everything one invocation printed is written down whole, with the command line that produced it,
	/// and the answer names the file. The answer itself carries only the end of it.
	/// </summary>
	[TestMethod]
	public async Task EveryInvocationIsCapturedWhole()
	{
		using var installer = Driver("products");

		var outcome = await installer.ListAsync(string.Empty, CancellationToken);

		IsFalse(string.IsNullOrEmpty(outcome.LogPath), "the answer names no capture of the output.");
		IsTrue(File.Exists(outcome.LogPath), $"the capture the answer names is not there: {outcome.LogPath}");

		var captured = await File.ReadAllTextAsync(outcome.LogPath, CancellationToken);

		IsTrue(captured.Contains("Products", StringComparison.Ordinal),
			"the capture does not say what the console was asked to do.");

		IsTrue(captured.Contains("exit code: 0", StringComparison.Ordinal), captured);
		IsTrue(captured.Contains("StockSharp.Designer", StringComparison.Ordinal), captured);

		IsTrue(outcome.Took > TimeSpan.Zero, "an invocation that ran a program took no time at all.");
	}

	private ConsoleProductInstaller Driver(string behaviour)
	{
		Environment.SetEnvironmentVariable(BehaviourVariable, behaviour);

		return new(Options(Stub()));
	}

	private ProductInstallerOptions Options(string console)
		=> new(
			LookedIn: [console],
			InstallRoot: Path.Combine(_root, "products"),
			AccountFile: _account,
			AllowedProducts: [9, 10],

			// Named as nothing, so the check against a foreign installer does not decide a test by what
			// the machine running it happens to have open. It has a test of its own.
			ForeignProcesses: [],
			ReadDeadline: TimeSpan.FromSeconds(60),
			ChangeDeadline: TimeSpan.FromSeconds(60),
			RemoveDeadline: TimeSpan.FromSeconds(60));

	/// <summary>The five methods that act, so a refusal can be asserted of all of them at once.</summary>
	/// <param name="installer">Installer to call.</param>
	/// <returns>One call each.</returns>
	private IEnumerable<Func<Task>> Calls(ConsoleProductInstaller installer)
	{
		yield return () => installer.ListAsync(string.Empty, CancellationToken).AsTask();
		yield return () => installer.InstalledAsync(string.Empty, CancellationToken).AsTask();
		yield return () => installer.InstallAsync(9, reinstall: false, CancellationToken).AsTask();
		yield return () => installer.UpdateAsync(9, backupSettings: false, CancellationToken).AsTask();
		yield return () => installer.RemoveAsync(9, removeData: false, CancellationToken).AsTask();
	}

	private static string[] Arguments(ProductOutcome outcome)
		=> [.. outcome.Unparsed
			.Where(line => line.StartsWith(ArgumentPrefix, StringComparison.Ordinal))
			.Select(line => line[ArgumentPrefix.Length..])];

	private static string Line(ProductOutcome outcome, string prefix)
	{
		var line = outcome.Unparsed.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));

		return line is null ? string.Empty : line[prefix.Length..];
	}

	private static string Describe(ProductOutcome outcome)
		=> $"exit {outcome.ExitCode}, products [{string.Join(" | ", outcome.Products.Select(p => p.Name))}], " +
			$"unparsed [{string.Join(" | ", outcome.Unparsed)}]";

	private string Missing()
		=> Path.Combine(_root, $"StockSharp.Installer.Console-{Guid.NewGuid():n}.exe");

	/// <summary>
	/// Where the misbehaving installer was built. It is reached by path rather than by reference, which
	/// is the whole point of driving a program rather than linking it.
	/// </summary>
	private static string Stub()
	{
		var configuration = AppContext.BaseDirectory.Contains(
			$"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
			StringComparison.OrdinalIgnoreCase)
			? "Debug"
			: "Release";

		var candidate = Path.Combine(
			RepositoryRoot, "tests", "Odysseus.InstallerStub", "bin", configuration, "net10.0",
			OperatingSystem.IsWindows() ? "Odysseus.InstallerStub.exe" : "Odysseus.InstallerStub");

		if (!File.Exists(candidate))
		{
			Fail("The misbehaving installer was not found where the build puts it. This project builds it, so its absence is a broken build or a wrong path.");
		}

		return candidate;
	}
}
