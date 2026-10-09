namespace StockSharp.Odysseus.Engine.Tests;

using System.Diagnostics;

/// <summary>
/// What a session makes of a process it did not start.
/// </summary>
/// <remarks>
/// A deployment now outlives the session that started it, which turns "is it running" from a lookup in a
/// dictionary into a question about the world. The four answers are not equally cheap to be wrong about:
/// reading an alive-and-silent runner as gone tells a person nobody is holding a position while somebody
/// is, and that is the report that gets acted on.
///
/// None of this touches a broker. The far end is a stub that speaks the protocol and misbehaves on
/// demand, because the three interesting outcomes - a crash, a silence, a build that does not match -
/// cannot be produced on request by the real thing.
/// </remarks>
[TestClass]
public class RunnerLauncherTests : OdysseusTestBase
{
	private string _root;
	private string _one;
	private string _two;
	private string _other;

	private static RunnerConnectorPolicy Policy
		=> new(Path.Combine(Path.GetTempPath(), "odysseus-connectors"), ["https://example.invalid"], ["StockSharp."]);

	/// <summary>Makes a projects root of its own, and the deployment identifiers this test will use.</summary>
	[TestInitialize]
	public void CreateRoot()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-launcher", Guid.NewGuid().ToString("n"));
		_one = Id("one");
		_two = Id("two");
		_other = Id("other");
	}

	/// <summary>Removes it, and anything it left running.</summary>
	[TestCleanup]
	public void DeleteRoot()
	{
		foreach (var record in Registry().Records())
			Kill(record.ProcessId);

		if (Directory.Exists(_root))
		{
			try
			{
				Directory.Delete(_root, recursive: true);
			}
			catch (IOException)
			{
				// A stub still letting go of its own log. The directory is under the temporary folder and
				// its name is a fresh identifier, so nothing later depends on it having gone.
			}
		}
	}

	/// <summary>A runner that greets is attached, and what it says comes back whole.</summary>
	[TestMethod]
	public async Task ARunnerThatGreetsIsAttachedAndAnswers()
	{
		var launcher = Launcher();

		var launched = await launcher.LaunchAsync(Launch(_one), CancellationToken);

		AreEqual(RunnerStatuses.Attached, launched.Status, launched.Detail);
		AreEqual(TradingModes.Paper, launched.Mode);
		IsTrue(launched.ProcessId > 0, "a runner was attached without a process behind it.");

		// A second launcher over the same root, holding nothing the first one held: this is a later
		// session, in a different process, finding a deployment it did not start.
		var again = await Launcher().AttachAsync(_one, CancellationToken);

		AreEqual(RunnerStatuses.Attached, again.Status, again.Detail);
		AreEqual(3m, again.State.Position, "the position did not survive being read by another session.");
		AreEqual(1, again.State.WorkingOrders);
		AreEqual("stub-account", again.State.Account);
	}

	/// <summary>
	/// A runner whose process ended is reported as gone, with the last thing it wrote down rather than
	/// with whatever the last observation happened to catch.
	/// </summary>
	[TestMethod]
	public async Task ARunnerWhoseProcessEndedIsGoneAndSaysWhatItLastHeld()
	{
		var home = Prepare(_one, "crash");

		// It writes its record, journals a position, and dies - which is what a crash leaves behind.
		await RunAsync(home);

		var found = await Launcher().AttachAsync(_one, CancellationToken);

		AreEqual(RunnerStatuses.Gone, found.Status, found.Detail);
		IsNull(found.State, "a runner that is not there reported a state.");

		IsTrue(found.Detail.Contains("3", StringComparison.Ordinal),
			$"the report does not carry the position it was last known to hold: {found.Detail}");
	}

	/// <summary>
	/// A runner that is alive and not answering is not gone, and is never reported as gone. Only one of
	/// the two means nobody is holding a position.
	/// </summary>
	[TestMethod]
	public async Task ARunnerThatIsAliveAndSilentIsNotReportedAsGone()
	{
		var home = Prepare(_one, "silent");

		using var process = Start(home);

		await Recorded(home);

		var found = await Launcher().AttachAsync(_one, CancellationToken);

		AreEqual(RunnerStatuses.Unresponsive, found.Status, found.Detail);

		IsTrue(found.Detail.Contains("may still be", StringComparison.OrdinalIgnoreCase),
			$"the report does not say what is still possible: {found.Detail}");
	}

	/// <summary>
	/// Stopping a runner that is alive and not answering writes nothing down and refuses, because
	/// recording it as stopped would say something untrue about an open position.
	/// </summary>
	[TestMethod]
	public async Task StoppingAnUnresponsiveRunnerRecordsNothingAndRefuses()
	{
		var home = Prepare(_one, "silent");

		using var process = Start(home);

		await Recorded(home);

		var refusal = await ThrowsAsync<RunnerUnresponsiveException>(
			() => Launcher().StopAsync(_one, closePosition: true, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("not answering", StringComparison.OrdinalIgnoreCase),
			$"the refusal does not say what is wrong: {refusal.Message}");

		IsTrue(refusal.Message.Contains("Nothing was recorded", StringComparison.OrdinalIgnoreCase),
			$"the refusal does not say that nothing was written down: {refusal.Message}");

		IsNotNull(Registry().Read(_one), "the runner's record was removed although nothing stopped it.");
	}

	/// <summary>A stop carries the decision about the position, and the runner acts on that decision.</summary>
	[TestMethod]
	public async Task AStopCarriesTheDecisionAboutThePosition()
	{
		var launcher = Launcher();

		await launcher.LaunchAsync(Launch(_one), CancellationToken);

		var ended = await launcher.StopAsync(_one, closePosition: true, CancellationToken);

		AreEqual(RunnerStatuses.Attached, ended.Status, ended.Detail);
		AreEqual(0m, ended.State.Position, "the position was left open although the stop was to close it.");
		AreEqual(0, ended.State.WorkingOrders, "orders were left working, which nobody asked for.");
		IsFalse(ended.State.IsRunning);
	}

	/// <summary>A stop that leaves the position open leaves it open, and says so.</summary>
	[TestMethod]
	public async Task AStopThatLeavesThePositionOpenLeavesIt()
	{
		var launcher = Launcher();

		await launcher.LaunchAsync(Launch(_one), CancellationToken);

		var ended = await launcher.StopAsync(_one, closePosition: false, CancellationToken);

		AreEqual(3m, ended.State.Position, "the position was closed although the stop said to leave it.");
	}

	/// <summary>Stopping frees the project, so the next deployment on it is not refused.</summary>
	[TestMethod]
	public async Task StoppingGivesTheProjectBack()
	{
		var launcher = Launcher();

		await launcher.LaunchAsync(Launch(_one), CancellationToken);
		await launcher.StopAsync(_one, closePosition: false, CancellationToken);

		var second = await launcher.LaunchAsync(Launch(_two), CancellationToken);

		AreEqual(RunnerStatuses.Attached, second.Status, second.Detail);
	}

	/// <summary>
	/// A second deployment on a project a runner already has is refused before anything is started.
	/// </summary>
	[TestMethod]
	public async Task ASecondDeploymentOnOneProjectIsRefused()
	{
		var launcher = Launcher();

		await launcher.LaunchAsync(Launch(_one), CancellationToken);

		var refusal = await ThrowsAsync<RunnerAlreadyRunningException>(
			() => launcher.LaunchAsync(Launch(_two), CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains(_one, StringComparison.Ordinal),
			$"the refusal does not say what already has the project: {refusal.Message}");

		IsNull(Registry().Read(_two), "the refused deployment left a runner behind anyway.");
	}

	/// <summary>
	/// A runner from another platform build is not talked to at all: it is neither read nor stopped, and
	/// nothing about the deployment is written down.
	/// </summary>
	[TestMethod]
	public async Task ARunnerFromAnotherBuildIsNotTalkedTo()
	{
		var home = Prepare(_one, "answer", engine: "another-engine");

		using var process = Start(home);

		await Recorded(home);

		var found = await Launcher().AttachAsync(_one, CancellationToken);

		AreEqual(RunnerStatuses.Unknown, found.Status, found.Detail);

		IsTrue(found.Detail.Contains("another-engine", StringComparison.Ordinal),
			$"the report does not say which build answered: {found.Detail}");
	}

	/// <summary>
	/// Something else answering to a runner's pipe name is refused. A pipe name is derived from the
	/// deployment, so a stale record plus a reused name would otherwise put a stop into another strategy.
	/// </summary>
	[TestMethod]
	public async Task SomethingElseAnsweringToTheNameIsRefused()
	{
		var home = Prepare(_one, "answer", deployment: _other);

		using var process = Start(home);

		await Recorded(home);

		var found = await Launcher().AttachAsync(_one, CancellationToken);

		AreEqual(RunnerStatuses.Unknown, found.Status, found.Detail);

		IsTrue(found.Detail.Contains(_other, StringComparison.Ordinal),
			$"the report does not say what was actually on the other end: {found.Detail}");
	}

	/// <summary>A deployment nothing was ever recorded for is unknown, and is still an answer.</summary>
	[TestMethod]
	public async Task ADeploymentWithNoRunnerAtAllIsAnAnswerRatherThanAFailure()
	{
		var found = await Launcher().AttachAsync(Id("missing"), CancellationToken);

		AreEqual(RunnerStatuses.Unknown, found.Status);
		IsTrue(found.Detail.Length > 0, "nothing was said about a deployment that has no runner.");
	}

	/// <summary>
	/// A runner that is not where the deployment says it is is this server's failure, named as such, with
	/// the variable that would fix it.
	/// </summary>
	[TestMethod]
	public async Task ARunnerThatIsNotThereIsNamedAsSuch()
	{
		var missing = Path.Combine(Path.GetTempPath(), $"odysseus-runner-{Guid.NewGuid():n}.exe");

		var launcher = new RunnerLauncher(Options(missing), Registry(), Policy, "stub-engine");

		var failure = await ThrowsAsync<RunnerStartFailedException>(
			() => launcher.LaunchAsync(Launch(_one), CancellationToken).AsTask());

		IsTrue(failure.Message.Contains(RunnerOptions.PathVariable, StringComparison.Ordinal),
			$"the failure does not name the variable that would fix it: {failure.Message}");

		IsNull(Registry().HolderOf("prj_one"), "a launch that started nothing kept the project.");
	}

	/// <summary>
	/// The mandate variable is removed from every runner this launcher starts, and is set only from what
	/// the caller explicitly passed. An operator who exported it in the shell that started a server still
	/// gets paper runners from it: live never arrives by inheritance.
	/// </summary>
	[TestMethod]
	public void LiveModeIsNeverInheritedFromTheShell()
	{
		var home = Registry().Home(_one);

		var inherited = RunnerLauncher.Describe(Options("runner.exe"), home, string.Empty);

		IsFalse(inherited.Environment.ContainsKey(LiveMandateFile.PathVariable),
			"the runner would inherit the mandate variable from whatever shell started this process.");

		var asked = RunnerLauncher.Describe(Options("runner.exe"), home, "/etc/odysseus/mandate.json");

		AreEqual("/etc/odysseus/mandate.json", asked.Environment[LiveMandateFile.PathVariable],
			"a mandate the caller passed explicitly did not reach the runner.");
	}

	/// <summary>
	/// The phrase that confirms a live mandate is removed from every runner this launcher starts, exactly
	/// as the mandate itself is, and is never set from anything here. So a runner a host started cannot
	/// reach a real account even if somebody handed it a mandate path: it has no phrase, and no terminal
	/// to be asked for one at. Live trading is a person starting the runner themselves.
	/// </summary>
	[TestMethod]
	public void TheConfirmationPhraseIsNeverInheritedFromTheShell()
	{
		var home = Registry().Home(_one);

		var mandate = Environment.GetEnvironmentVariable(LiveMandateFile.PathVariable);
		var phrase = Environment.GetEnvironmentVariable(LiveMandateConfirmation.PhraseVariable);

		try
		{
			// Exported here, so that removing them is what the assertions below observe rather than the
			// machine happening not to have had them set.
			Environment.SetEnvironmentVariable(LiveMandateFile.PathVariable, "/etc/odysseus/exported.json");
			Environment.SetEnvironmentVariable(LiveMandateConfirmation.PhraseVariable, "trade real money on U1234567");

			var inherited = RunnerLauncher.Describe(Options("runner.exe"), home, string.Empty);

			IsFalse(inherited.Environment.ContainsKey(LiveMandateConfirmation.PhraseVariable),
				"the runner would inherit the phrase confirming a live mandate from the shell that started this process.");

			IsFalse(inherited.Environment.ContainsKey(LiveMandateFile.PathVariable),
				"the runner would inherit the mandate itself from the shell that started this process.");

			var asked = RunnerLauncher.Describe(Options("runner.exe"), home, "/etc/odysseus/asked.json");

			IsFalse(asked.Environment.ContainsKey(LiveMandateConfirmation.PhraseVariable),
				"a caller that passed a mandate path got the phrase confirming it passed along too.");

			AreEqual("/etc/odysseus/asked.json", asked.Environment[LiveMandateFile.PathVariable],
				"a mandate the caller passed explicitly did not reach the runner.");
		}
		finally
		{
			Environment.SetEnvironmentVariable(LiveMandateFile.PathVariable, mandate);
			Environment.SetEnvironmentVariable(LiveMandateConfirmation.PhraseVariable, phrase);
		}
	}

	/// <summary>
	/// The runner is given the credentials and denied the projects root - the exact inverse of the worker,
	/// which is denied the credentials because it must never reach an account.
	/// </summary>
	[TestMethod]
	public void TheRunnerKeepsTheCredentialsAndLosesTheProjects()
	{
		var described = RunnerLauncher.Describe(Options("runner.exe"), Registry().Home(_one), string.Empty);

		IsFalse(described.Environment.ContainsKey("ODYSSEUS_PROJECTS_ROOT"),
			"the runner was told where the projects are, and it has no business in their metadata.");

		IsFalse(described.RedirectStandardOutput || described.RedirectStandardError || described.RedirectStandardInput,
			"the runner's own streams were redirected into a parent that is going to exit before it does.");
	}

	/// <summary>
	/// The runner's home carries everything it needs and nothing secret: the plan names the connector and
	/// the token, and the assembly is written beside it.
	/// </summary>
	[TestMethod]
	public async Task TheRunnersHomeCarriesThePlanAndTheAssembly()
	{
		await Launcher().LaunchAsync(Launch(_one), CancellationToken);

		var home = Registry().Home(_one);
		var plan = home.ReadPlan();

		AreEqual(_one, plan.DeploymentId);
		AreEqual("prj_one", plan.ProjectId);
		AreEqual("AAPL", plan.Symbol);
		AreEqual(10m, plan.Volume);
		AreEqual(RunnerProtocol.PipeOf(_one), plan.Pipe);
		AreEqual(32, plan.Token.Length, "the token is not the length a fresh one should be.");
		AreEqual(9m, plan.Parameters["fast"]);

		IsTrue(File.Exists(home.AssemblyFile), "the assembly being traded was not written into the home.");
	}

	/// <summary>
	/// A deployment identifier of this test's own.
	/// </summary>
	/// <param name="stem">What the identifier is for, so a failure names the runner it means.</param>
	/// <returns>The identifier.</returns>
	/// <remarks>
	/// Fresh per test rather than a literal shared with the test beside it. A runner's pipe name is
	/// derived from the deployment and a named pipe is machine-wide, so two tests holding one identifier
	/// are two processes fighting over one pipe - and the loser reports the winner's runner, which is a
	/// situation the product cannot be in. Identifiers are issued (<c>dep_</c> and a GUID) and no two
	/// deployments carry the same one, so unique here is what the product does rather than a precaution.
	/// </remarks>
	private static string Id(string stem)
		=> $"dep_{stem}_{Guid.NewGuid():n}";

	/// <summary>
	/// Deadlines short enough that a test which is meant to give up gives up quickly. Everything here is
	/// a local process and a local pipe, so seconds are generous.
	/// </summary>
	private static RunnerOptions Options(string path)
		=> new(
			path,
			Arguments: [],
			HandshakeDeadline: TimeSpan.FromSeconds(20),
			CallDeadline: TimeSpan.FromSeconds(2),
			StopDeadline: TimeSpan.FromSeconds(10),
			Heartbeat: TimeSpan.FromSeconds(15));

	private static RunnerLaunch Launch(string deploymentId)
		=> new(
			deploymentId,
			"prj_one",
			"cnd_one",
			"Generated",
			[1, 2, 3],
			new Dictionary<string, decimal>(StringComparer.Ordinal) { ["fast"] = 9m, ["slow"] = 21m },
			"AAPL",
			TimeSpan.FromMinutes(5),
			10m,
			new("StockSharp.Stub", "1.0.0", string.Empty, null),
			string.Empty);

	private static void Kill(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);

			process.Kill(entireProcessTree: true);
		}
		catch (Exception error) when (error is ArgumentException or InvalidOperationException)
		{
			// It had already gone, which is the outcome that was wanted.
		}
	}

	/// <summary>
	/// Where the misbehaving runner was built. It is reached by path rather than by reference, which is
	/// the whole point of a runner.
	/// </summary>
	private static string Stub()
	{
		var configuration = AppContext.BaseDirectory.Contains(
			$"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
			StringComparison.OrdinalIgnoreCase)
			? "Debug"
			: "Release";

		var candidate = Path.Combine(
			RepositoryRoot, "tests", "Odysseus.RunnerStub", "bin", configuration, "net10.0",
			OperatingSystem.IsWindows() ? "Odysseus.RunnerStub.exe" : "Odysseus.RunnerStub");

		return File.Exists(candidate) ? candidate : null;
	}

	private RunnerRegistry Registry() => new(_root, SystemProcessProbe.Instance);

	private RunnerLauncher Launcher()
	{
		var stub = Stub();

		if (stub is null)
			Fail("The misbehaving runner was not found where the build puts it. This project builds it, so its absence is a broken build or a wrong path.");

		return new(Options(stub), Registry(), Policy, "stub-engine");
	}

	/// <summary>
	/// Writes a home the stub can be started against directly, for the cases where the launcher must not
	/// be the thing that starts it - a runner that has to survive being read, or one that has to already
	/// be dead.
	/// </summary>
	private RunnerHome Prepare(string deploymentId, string behaviour, string engine = "", string deployment = "")
	{
		var home = Registry().Claim("prj_one", deploymentId);

		home.WritePlan(new(
			RunnerHome.Schema,
			deploymentId,
			"prj_one",
			"cnd_one",
			"Generated",
			"strategy.dll",
			"AAPL",
			TimeSpan.FromMinutes(5),
			10m,
			new Dictionary<string, decimal>(StringComparer.Ordinal),
			new("StockSharp.Stub", "1.0.0", string.Empty, null),
			Policy.CacheRoot,
			Policy.Sources,
			Policy.Allow,
			RunnerProtocol.PipeOf(deploymentId),
			"0123456789abcdef0123456789abcdef",
			TimeSpan.FromSeconds(15)));

		// Named as a literal rather than through the stub's own constant: a test project may not link an
		// executable it is not the test project of, and reaching one by path is the whole point of it.
		File.WriteAllLines(Path.Combine(home.Directory, "behaviour.txt"), [behaviour, engine, deployment]);

		return home;
	}

	private Process Start(RunnerHome home)
	{
		var stub = Stub();

		if (stub is null)
			Fail("The misbehaving runner was not found where the build puts it. This project builds it, so its absence is a broken build or a wrong path.");

		return Process.Start(new ProcessStartInfo(stub) { UseShellExecute = false, ArgumentList = { home.Directory } });
	}

	/// <summary>Starts the stub and waits for it to finish, which is how a crash is arranged.</summary>
	private async Task RunAsync(RunnerHome home)
	{
		using var process = Start(home);

		await process.WaitForExitAsync(CancellationToken);
	}

	/// <summary>Waits until the runner has written down how to find it.</summary>
	private async Task Recorded(RunnerHome home)
	{
		for (var waited = 0; waited < 200; waited++)
		{
			if (home.ReadRecord() is not null)
				return;

			await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken);
		}

		Fail($"The stub never wrote a record into {home.Directory}.");
	}
}
