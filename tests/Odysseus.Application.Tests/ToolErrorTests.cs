namespace Odysseus.Application.Tests;

using System.Text.Json;

using Odysseus.Spec;

/// <summary>
/// What an agent is told when a call fails.
/// </summary>
/// <remarks>
/// These assertions are deliberately about the text. The message is not a log line, it is the next
/// prompt: an agent told only that something went wrong has nothing to aim at and burns its remaining
/// attempts guessing. So the tests check that a refusal names what was wrong and what a correct value
/// would look like, not merely that a refusal happened.
/// </remarks>
[TestClass]
public class ToolErrorTests : OdysseusTestBase
{
	private const string Correlation = "abc123";

	/// <summary>
	/// The category vocabulary is published, and an agent branches on it. A value that exists on one
	/// side only is either a branch that never runs or a message nobody can interpret.
	/// </summary>
	[TestMethod]
	public void CategoriesMatchThePublishedContract()
	{
		var schema = Path.Combine(RepositoryRoot, "schemas", "error.schema.json");

		using var document = JsonDocument.Parse(File.ReadAllText(schema));

		var published = document.RootElement
			.GetProperty("properties")
			.GetProperty("category")
			.GetProperty("enum")
			.EnumerateArray()
			.Select(v => v.GetString())
			.ToArray();

		var declared = Enum.GetNames<ToolErrorCategories>();

		CollectionAssert.AreEquivalent(published, declared,
			"the error categories of the contract and of the code have drifted apart.");
	}

	/// <summary>
	/// A refused specification arrives as a category of its own, with every problem and a pointer at the
	/// tool that reports the same list for free.
	/// </summary>
	[TestMethod]
	public void ARefusedSpecificationListsEveryProblem()
	{
		var described = ToolErrors.Describe(
			new InvalidSpecException(
			[
				new SpecProblem("entries.e1.condition", "refers to 'Undeclared'", "declare it"),
				new SpecProblem("exits", "never closes a position", "add an exit"),
			]),
			Correlation);

		AreEqual(ToolErrorCategories.SpecValidation, described.Category);

		IsTrue(described.Message.Contains("entries.e1.condition", StringComparison.Ordinal),
			"the place of each problem must survive the mapping.");

		IsTrue(described.Message.Contains("exits", StringComparison.Ordinal),
			"every problem must survive, not only the first: a caller should fix them in one attempt.");

		IsTrue(described.Remediation.Contains("validate_spec", StringComparison.Ordinal),
			"the caller must be pointed at the tool that reports the same list without recording anything.");
	}

	/// <summary>
	/// Source that will not compile is our defect, not the caller's, and the answer has to say so along
	/// with the code that failed.
	/// </summary>
	/// <remarks>
	/// The agent never wrote this C#; the translator did. Reported as an unspecified internal failure it
	/// reads as something to retry, and there is nothing to retry — the same specification will emit the
	/// same broken source forever. What the agent can do is report it, so the answer carries the compiler
	/// identifier, the line and the text, and says whose fault it is.
	/// </remarks>
	[TestMethod]
	public void SourceThatWillNotCompileNamesTheCodeAndTheCulprit()
	{
		var described = ToolErrors.Describe(
			new StrategyBuildException(
			[
				new BuildProblem("CS0103", "The name 'Atr' does not exist in the current context", 42),
				new BuildProblem("CS1002", "; expected", 57),
			]),
			Correlation);

		AreEqual(ToolErrorCategories.CompilationFailed, described.Category);

		IsTrue(described.Message.Contains("CS0103", StringComparison.Ordinal) &&
			described.Message.Contains("CS1002", StringComparison.Ordinal),
			$"every diagnostic must survive the mapping: {described.Message}");

		IsTrue(described.Message.Contains("42", StringComparison.Ordinal),
			$"the line of the generated source must survive: {described.Message}");

		IsTrue(described.Remediation.Contains(Correlation, StringComparison.Ordinal),
			"a defect of ours needs the correlation id to be reportable.");

		IsTrue(described.Remediation.Contains("translator", StringComparison.Ordinal),
			$"the caller must be told it did not write this code: {described.Remediation}");
	}

	/// <summary>
	/// A rule of our own that the generated code broke is reported apart from a compiler error, because
	/// they are read differently: one is code that cannot run, the other is code that must not.
	/// </summary>
	[TestMethod]
	public void ABrokenAnalyzerRuleIsNotACompilerError()
	{
		var described = ToolErrors.Describe(
			new StrategyBuildException([new BuildProblem("ODSTR0004", "A strategy may not read the clock", 12)]),
			Correlation);

		AreEqual(ToolErrorCategories.AnalyzerViolation, described.Category);

		IsTrue(described.Message.Contains("ODSTR0004", StringComparison.Ordinal),
			$"the rule that was broken must survive: {described.Message}");
	}

	/// <summary>
	/// Asking for something that needs a broker on a server started without one is a configuration
	/// answer, not a defect and not a bad request.
	/// </summary>
	[TestMethod]
	public void AMissingBrokerIsAConfigurationAnswer()
	{
		var described = ToolErrors.Describe(
			new BrokerNotConfiguredException("Downloading history"), Correlation);

		AreEqual(ToolErrorCategories.ConfigurationInvalid, described.Category);

		IsTrue(described.Message.Contains("broker account", StringComparison.Ordinal),
			$"the answer must say what is missing: {described.Message}");

		IsTrue(described.Remediation.Contains("ODYSSEUS_BROKER_CONNECTOR", StringComparison.Ordinal),
			$"the answer must say how to supply it: {described.Remediation}");

		IsTrue(described.Remediation.Contains("import_demo_dataset", StringComparison.Ordinal),
			$"the caller must be told what it can still do without an account: {described.Remediation}");
	}

	/// <summary>
	/// A machine with no installer on it is the ordinary case, and it is answered the way a missing
	/// broker is: what is missing, how an operator would supply it, and what still works without it.
	/// </summary>
	/// <remarks>
	/// The last part is what stops the answer from reading as a defect. Almost no machine has the
	/// StockSharp installer on it, this server never fetches one, and no part of the research loop needs
	/// a product - so an agent that meets this refusal has lost nothing it was going to use.
	/// </remarks>
	[TestMethod]
	public void AMissingInstallerIsAConfigurationAnswer()
	{
		var described = ToolErrors.Describe(
			new ProductInstallerUnavailableException(
				"The StockSharp installer console is not on this machine, so no product can be installed."),
			Correlation);

		AreEqual(ToolErrorCategories.ConfigurationInvalid, described.Category);

		IsTrue(described.Message.Contains("not on this machine", StringComparison.Ordinal),
			$"the answer must say what is missing: {described.Message}");

		IsTrue(described.Remediation.Contains("ODYSSEUS_INSTALLER", StringComparison.Ordinal),
			$"the answer must name the variable that would supply it: {described.Remediation}");

		IsTrue(described.Remediation.Contains("ODYSSEUS_PRODUCT_ALLOW", StringComparison.Ordinal),
			$"the answer must name the other half of the configuration: {described.Remediation}");

		IsTrue(described.Remediation.Contains("nothing here downloads", StringComparison.OrdinalIgnoreCase),
			$"the answer must say that this server does not fetch the program: {described.Remediation}");

		IsTrue(described.Remediation.Contains("get_installer_state", StringComparison.Ordinal),
			$"the caller must be pointed at the tool that reports all of this for free: {described.Remediation}");
	}

	/// <summary>
	/// Another installer holding the machine is a conflict rather than a configuration problem, because
	/// the answer to it is to wait or to close a window rather than to change anything.
	/// </summary>
	[TestMethod]
	public void AForeignInstallerIsAConflict()
	{
		var described = ToolErrors.Describe(
			new ProductInstallerBusyException("StockSharp.Installer.UI is already running on this machine."),
			Correlation);

		AreEqual(ToolErrorCategories.Conflict, described.Category);

		IsTrue(described.Message.Contains("already running", StringComparison.Ordinal),
			$"the answer must say what is holding the machine: {described.Message}");

		IsTrue(described.Remediation.Contains("Nothing was started", StringComparison.Ordinal),
			$"the caller must be told that asking again is safe: {described.Remediation}");
	}

	/// <summary>
	/// An installer that overran cannot promise that nothing was written, and says so. Every other
	/// timeout in this product can, which is exactly why this one has an answer of its own.
	/// </summary>
	[TestMethod]
	public void AnInstallerThatOverranDoesNotPromiseThatNothingWasWritten()
	{
		var installer = ToolErrors.Describe(
			new ProductInstallerTimedOutException("The installer was stopped after 20 minutes."),
			Correlation);

		AreEqual(ToolErrorCategories.Timeout, installer.Category);

		IsTrue(installer.Remediation.Contains("partial installation", StringComparison.Ordinal),
			$"the caller must be told the product directory may be half written: {installer.Remediation}");

		var broker = ToolErrors.Describe(new TimeoutException("The broker did not answer."), Correlation);

		AreEqual(ToolErrorCategories.Timeout, broker.Category);

		IsTrue(broker.Remediation.Contains("Nothing was written", StringComparison.Ordinal),
			"the ordinary timeout must keep the promise the installer one cannot make: " + broker.Remediation);
	}

	/// <summary>
	/// Every failure this product raises on purpose is answered on purpose. Anything left unmapped falls
	/// through to Internal and reads as a defect of the server, which sends an agent to report a bug when
	/// what it actually did was ask for something that is not there.
	/// </summary>
	/// <remarks>
	/// The reflection half is what keeps this true. Three of these had been declared, thrown and never
	/// mapped, and nothing said so: each one only looked wrong from the far end of a tool call.
	/// </remarks>
	[TestMethod]
	public void EveryFailureWeRaiseOnPurposeIsAnsweredOnPurpose()
	{
		Exception[] ours =
		[
			new ProjectNotFoundException(ProjectId.New()),
			new SpecNotFoundException(SpecId.New()),
			new DatasetNotFoundException(DatasetId.New()),
			new CandidateNotFoundException(CandidateId.New()),
			new RunNotFoundException(RunId.New()),
			new DeploymentNotFoundException(DeploymentId.New()),
			new ArtifactNotFoundException(ArtifactId.FromContent("x"u8)),
			new ArtifactCorruptedException(ArtifactId.FromContent("x"u8), "deadbeef"),
			new InvalidSpecException([new SpecProblem("where", "what", "fix")]),
			new ResearchBudgetExhaustedException("spent"),
			new StrategyBuildException([new BuildProblem("CS0103", "no such name", 3)]),
			new BrokerNotConfiguredException("Downloading history"),
			new ConnectorNotPaperException("Example.Adapter has no way of being told it is on a paper account."),
			new ConnectorRefusedException("Example.Adapter declares no setting called 'Fed'."),
			new ServerModeException("Choosing a connector is not something a hosted instance allows."),
			new ProductInstallerUnavailableException("The StockSharp installer console is not on this machine."),
			new ProductInstallerBusyException("StockSharp.Installer.UI is already running on this machine."),
			new ProductInstallerTimedOutException("The installer was stopped after 20 minutes."),
			new IsolationFailedException(IsolationFailures.Timeout, "The run was stopped after 5 minutes."),
			new IsolationFailedException(IsolationFailures.Memory, "The run was stopped after using 2048 MB."),
			new IsolationFailedException(IsolationFailures.Crashed, "The worker exited without answering."),
			new IsolationFailedException(IsolationFailures.Handshake, "The worker speaks protocol 2."),
			new HarnessFailedException(HarnessFailures.Worker, "The worker exited without answering.", new IOException("pipe")),
			new HarnessFailedException(HarnessFailures.Storage, "What the run produced could not be written down.", new IOException("disk")),
			new RunnerUnresponsiveException("Process 24188 is alive and not answering."),
			new RunnerStopFailedException("dep_one did not stop: the venue would not take the cancellation."),
			new RunnerAlreadyRunningException("dep_one is already running on this project."),
			new RunnerStartFailedException("The runner at ... would not start."),
			new LiveMandateInvalidException("The mandate at '...' expired at 2026-09-30T00:00:00Z."),
		];

		foreach (var failure in ours)
		{
			var described = ToolErrors.Describe(failure, Correlation);

			AreNotEqual(ToolErrorCategories.Internal, described.Category,
				$"{failure.GetType().Name} is reported as a defect of the server rather than as what it is.");

			IsFalse(string.IsNullOrWhiteSpace(described.Remediation),
				$"{failure.GetType().Name} is answered without saying what to do next.");
		}

		// The list above has to keep up with the code. An exception type we declare and forget to map is
		// exactly the defect this test exists for, so the two are compared rather than trusted.
		var declared = typeof(ToolErrors).Assembly
			.GetTypes()
			.Where(type => type.IsSubclassOf(typeof(Exception)) && type.IsPublic)
			.Select(type => type.Name)
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToArray();

		var covered = ours.Select(f => f.GetType().Name).Distinct().ToArray();

		var missed = declared.Except(covered, StringComparer.Ordinal).ToArray();

		AreEqual(0, missed.Length,
			$"declared but never checked against the error contract: {string.Join(", ", missed)}.");
	}

	/// <summary>A missing project says how to find one that exists.</summary>
	[TestMethod]
	public void MissingProjectPointsAtHowToFindOne()
	{
		var described = ToolErrors.Describe(new ProjectNotFoundException(ProjectId.New()), Correlation);

		AreEqual(ToolErrorCategories.NotFound, described.Category);
		IsTrue(described.Remediation.Contains("list_projects", StringComparison.Ordinal),
			"the remediation must name the tool that produces a valid identifier.");
	}

	/// <summary>A malformed argument keeps the message that says what a correct one looks like.</summary>
	[TestMethod]
	public void MalformedArgumentKeepsItsExplanation()
	{
		var described = ToolErrors.Describe(
			new ArgumentException("'not-an-id' is not a project identifier. Identifiers start with 'prj_'."),
			Correlation);

		AreEqual(ToolErrorCategories.InvalidRequest, described.Category);
		IsTrue(described.Message.Contains("prj_", StringComparison.Ordinal),
			"the explanation of what a correct value looks like must survive the mapping.");
	}

	/// <summary>A corrupted artifact says what it means for results that reference it.</summary>
	[TestMethod]
	public void CorruptionExplainsTheConsequence()
	{
		var described = ToolErrors.Describe(
			new ArtifactCorruptedException(ArtifactId.FromContent("x"u8), "deadbeef"),
			Correlation);

		AreEqual(ToolErrorCategories.IntegrityViolation, described.Category);
		IsTrue(described.Remediation.Contains("reproduced", StringComparison.Ordinal),
			"the caller must be told that results referencing the artifact are no longer reproducible.");
	}

	/// <summary>
	/// An unexpected defect is ours, and its message stays in the log: it can carry a path or a value
	/// the caller has no business seeing.
	/// </summary>
	[TestMethod]
	public void UnexpectedFailuresRevealNothing()
	{
		var secret = @"C:\Users\someone\projects\private\key.txt";
		var described = ToolErrors.Describe(new IOException($"could not read {secret}"), Correlation);

		AreEqual(ToolErrorCategories.Internal, described.Category);
		IsFalse(described.Message.Contains(secret, StringComparison.Ordinal),
			"an internal failure must not leak the machine's contents to the caller.");
		IsFalse(described.Remediation.Contains(secret, StringComparison.Ordinal));
		IsTrue(described.Remediation.Contains(Correlation, StringComparison.Ordinal),
			"the caller needs the correlation id to report the defect.");
	}

	/// <summary>
	/// Spending the allowance is a refusal, not a defect. Reported as a server fault it reads as
	/// something to retry, and an agent that retries a ceiling burns whatever is left of it.
	/// </summary>
	[TestMethod]
	public void SpendingTheAllowanceIsNotADefect()
	{
		var described = ToolErrors.Describe(
			new ResearchBudgetExhaustedException("A search evaluates up to 50 settings and this project has 12 left."),
			Correlation);

		AreEqual(ToolErrorCategories.BudgetExhausted, described.Category);

		IsTrue(described.Message.Contains("50 settings", StringComparison.Ordinal),
			"the refusal must carry the numbers behind it.");

		IsTrue(described.Remediation.Contains("ceiling", StringComparison.Ordinal),
			$"the caller must be told the limit will not move: {described.Remediation}");
	}

	/// <summary>
	/// A failure of this server's own machinery reads as something to ask again, and says so.
	/// </summary>
	/// <remarks>
	/// The categories were already there; what was missing was anything that could produce them for a
	/// backtest. Reported as the candidate having failed, an agent reads a flaky afternoon as a property
	/// of its hypothesis and drops a specification that was never tried - so the answer has to say that
	/// nothing was measured, that nothing was taken for it, and that the same call may be repeated.
	/// </remarks>
	/// <param name="kind">Which part of the machinery failed.</param>
	/// <param name="category">Category the caller branches on.</param>
	/// <param name="broke">What the caller is told broke.</param>
	[TestMethod]
	[DataRow(HarnessFailures.Worker, ToolErrorCategories.WorkerFailure, "The worker exited without answering.")]
	[DataRow(HarnessFailures.Storage, ToolErrorCategories.StorageFailure, "What the run produced could not be written down.")]
	public void AFailureOfOurOwnMachineryIsSomethingToAskAgain(
		HarnessFailures kind,
		ToolErrorCategories category,
		string broke)
	{
		var secret = @"C:\Users\someone\projects\private\key.txt";

		var described = ToolErrors.Describe(
			new HarnessFailedException(kind, broke, new IOException(secret)),
			Correlation);

		AreEqual(category, described.Category);

		AreNotEqual(ToolErrorCategories.Internal, described.Category,
			"a broken machine of ours must not be reported as an unspecified defect, which says nothing.");

		AreEqual(broke, described.Message, "the answer must say what broke.");

		IsFalse(described.Message.Contains(secret, StringComparison.Ordinal) ||
			described.Remediation.Contains(secret, StringComparison.Ordinal),
			"the failure underneath stays in the log; it can carry a path off this machine.");

		IsTrue(described.Remediation.Contains("again", StringComparison.Ordinal),
			$"the caller must be told the call can be repeated: {described.Remediation}");

		IsTrue(described.Remediation.Contains("not charged", StringComparison.Ordinal),
			$"the caller must be told the allowance was not spent on it: {described.Remediation}");
	}

	/// <summary>Cancellation is not a defect and must not read like one.</summary>
	[TestMethod]
	public void CancellationIsNotADefect()
	{
		var described = ToolErrors.Describe(new OperationCanceledException(), Correlation);

		AreEqual(ToolErrorCategories.Cancelled, described.Category);
		IsTrue(described.Remediation.Contains("half-written", StringComparison.Ordinal),
			"the caller must be told nothing was left in an intermediate state.");
	}

	/// <summary>Every failure carries the identifier that ties it to the server logs.</summary>
	[TestMethod]
	public void EveryFailureCarriesACorrelationId()
	{
		Exception[] failures =
		[
			new ProjectNotFoundException(ProjectId.New()),
			new ArgumentException("bad"),
			new InvalidOperationException("wrong state"),
			new OperationCanceledException(),
			new IOException("disk"),
		];

		foreach (var failure in failures)
			AreEqual(Correlation, ToolErrors.Describe(failure, Correlation).CorrelationId);
	}
}
