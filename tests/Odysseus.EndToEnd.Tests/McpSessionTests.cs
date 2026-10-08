namespace StockSharp.Odysseus.EndToEnd.Tests;

using StockSharp.Odysseus.TestKit;

/// <summary>
/// The server as an agent meets it: a real process, a real handshake, real tool calls over standard
/// input and output.
/// </summary>
/// <remarks>
/// Everything below this level is tested without a transport, which is faster and sharper. This suite
/// exists for the things only the whole thing can be wrong about: that the tools are actually
/// published, that answers survive serialisation, that a refusal reaches the caller as something it can
/// act on, and that nothing leaks on the way out.
/// </remarks>
[TestClass]
public class McpSessionTests : OdysseusTestBase
{
	private const string SoundSpec = """
		{
		  "name": "Volume confirmed breakout",
		  "thesis": "A close above a recent high carries on when participation confirms it.",
		  "allowLong": true,
		  "allowShort": false,
		  "timeFrame": "00:05:00",
		  "warmupBars": 61,
		  "entries": [
		    {
		      "id": "e1",
		      "direction": "Long",
		      "condition": {
		        "kind": "Compare",
		        "left": { "kind": "Field", "field": "Close" },
		        "operator": "GreaterThan",
		        "right": {
		          "kind": "Indicator",
		          "name": "highest",
		          "length": { "kind": "Parameter", "name": "BreakoutPeriod" },
		          "source": "High",
		          "offset": 1
		        }
		      }
		    }
		  ],
		  "exits": [
		    { "id": "x1", "kind": "AtrStop", "direction": "Long",
		      "length": { "kind": "Constant", "value": 14 },
		      "multiplier": { "kind": "Constant", "value": 2 } },
		    { "id": "x2", "kind": "SessionEnd", "direction": "Long" }
		  ],
		  "parameters": [
		    { "name": "BreakoutPeriod", "type": "Integer", "default": 20, "minimum": 10, "maximum": 60, "step": 5, "optimizable": true }
		  ],
		  "risk": { "maxPositionPercent": 0.1, "maxDailyLossPercent": 0.02 },
		  "assumptions": [],
		  "invalidationConditions": []
		}
		""";

	private const string BrokenSpec = """
		{
		  "name": "",
		  "thesis": "",
		  "allowLong": true,
		  "allowShort": false,
		  "timeFrame": "00:05:00",
		  "warmupBars": 2,
		  "entries": [
		    {
		      "id": "e1",
		      "direction": "Long",
		      "condition": {
		        "kind": "Compare",
		        "left": { "kind": "Field", "field": "Close" },
		        "operator": "GreaterThan",
		        "right": { "kind": "Parameter", "name": "Undeclared" }
		      }
		    }
		  ],
		  "exits": [],
		  "parameters": [],
		  "risk": { "maxPositionPercent": 0.1, "maxDailyLossPercent": 0.02 },
		  "assumptions": [],
		  "invalidationConditions": []
		}
		""";
	private McpSession _session;
	private string _root;

	/// <summary>Starts a server over a temporary projects root.</summary>
	[TestInitialize]
	public void StartServer()
	{
		var executable = McpSession.FindServer();

		if (executable is null)
			Fail("The server executable was not found where the build puts it. This project builds it, so its absence is a broken build or a wrong path.");

		_root = Path.Combine(Path.GetTempPath(), "odysseus-e2e", Guid.NewGuid().ToString("n"));
		_session = McpSession.Start(executable, _root);
	}

	/// <summary>Stops the server and removes its files.</summary>
	[TestCleanup]
	public void StopServer()
	{
		_session?.Dispose();

		if (_root is not null && Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>The handshake reports the StockSharp product identity and display name.</summary>
	[TestMethod]
	[Timeout(60000, CooperativeCancellation = true)]
	public void ServerReportsItsStockSharpIdentity()
	{
		AreEqual("StockSharp.Odysseus", _session.ServerInfo.GetProperty("name").GetString());
		AreEqual("StockSharp Odysseus", _session.ServerInfo.GetProperty("title").GetString());
		IsFalse(string.IsNullOrWhiteSpace(_session.ServerInfo.GetProperty("version").GetString()));
	}

	/// <summary>
	/// The server completes the handshake and publishes exactly this roster - no more, no less.
	/// </summary>
	/// <remarks>
	/// Equality rather than presence, and the whole roster rather than a sample of it. Presence is what
	/// let the README's count sit at thirty-five while the code published thirty-seven: two tools were
	/// added and nothing here changed colour. The groups are the ones the README lists, in its order, so
	/// a tool with no home in this list is also a tool the README does not account for.
	/// </remarks>
	[TestMethod]
	public async Task ServerPublishesItsTools()
	{
		string[] expected =
		[
			// Projects.
			"describe_server", "create_project", "list_projects", "get_project_state", "rename_project",
			"get_audit_log",

			// Data.
			"import_demo_dataset", "import_history", "get_dataset_report", "describe_split", "analyze_market",

			// Instrument lookup.
			"lookup_symbols", "lookup_option_contracts",

			// Hypotheses.
			"get_indicator_catalog", "validate_spec", "propose_spec", "list_specs",

			// Candidates.
			"build_candidate", "get_candidate_source", "list_candidates", "check_determinism",

			// Backtests.
			"run_backtest", "get_metrics", "get_trades", "list_runs", "explain_run",

			// Evaluations.
			"evaluate_candidate", "get_evaluation",

			// The seeded genetic search, and the walk-forward over it.
			"run_optimization", "run_walk_forward",

			// The closed-data measurement.
			"measure_on_closed_data", "get_closed_data_state",

			// Deployments, which run in processes of their own and outlive this session.
			"deploy_candidate", "get_deployment", "list_deployments", "stop_deployment",
			"get_account_state",

			// Completion.
			"complete_strategy", "list_completed_strategies",

			// Connectors: which broker this server talks to is chosen while it runs, because the server
			// cannot compile itself and a connector it was compiled against would be a product for one
			// broker.
			"list_connectors", "describe_connector", "select_connector",

			// Products: installing StockSharp software on the machine this server runs on, which is off
			// unless an operator named the products that may be installed.
			"get_installer_state", "list_products", "list_installed_products",
			"install_product", "update_product", "remove_product",
		];

		var published = await _session.ListToolsAsync(CancellationToken);

		AreEqual(expected.Length, expected.Distinct().Count(), "this list names the same tool twice.");
		AreEqual(published.Count, published.Distinct().Count(), "the server published the same name twice.");

		var missing = expected.Except(published).Order().ToArray();
		var unaccounted = published.Except(expected).Order().ToArray();

		IsTrue(missing.Length == 0 && unaccounted.Length == 0,
			$"the published roster is not the documented one. Expected but not published: {Describe(missing)}. " +
			$"Published but not expected: {Describe(unaccounted)}. Bring this list and the count in README.md " +
			"to whatever the server now publishes.");

		static string Describe(string[] names) => names.Length == 0 ? "none" : string.Join(", ", names);
	}

	/// <summary>
	/// Every tool has to explain itself well enough for an agent to choose it without trying it, since
	/// the description is all it has to go on.
	/// </summary>
	[TestMethod]
	public async Task EveryToolExplainsItself()
	{
		var described = await _session.CallAsync("tools/list", new { }, CancellationToken);

		foreach (var tool in described.GetProperty("tools").EnumerateArray())
		{
			var name = tool.GetProperty("name").GetString();
			var description = tool.TryGetProperty("description", out var value) ? value.GetString() : null;

			IsTrue(!string.IsNullOrWhiteSpace(description) && description.Length > 40,
				$"'{name}' publishes no usable description.");
		}
	}

	/// <summary>A project can be created, found again and listed.</summary>
	[TestMethod]
	public async Task ProjectSurvivesTheRoundTrip()
	{
		var created = await _session.ToolAsync("create_project",
			new { name = "NVDA breakout", operationKey = "e2e-1" }, CancellationToken);

		var id = created.GetProperty("projectId").GetString();

		AreEqual("NVDA breakout", created.GetProperty("name").GetString());
		AreEqual("Draft", created.GetProperty("status").GetString());

		var state = await _session.ToolAsync("get_project_state", new { projectId = id }, CancellationToken);

		AreEqual(id, state.GetProperty("projectId").GetString());

		var listed = await _session.ToolAsync("list_projects", new { }, CancellationToken);

		AreEqual(1, listed.GetProperty("projects").GetArrayLength());
	}

	/// <summary>A retry of the same intent does not produce a second project.</summary>
	[TestMethod]
	public async Task RepeatedCallWithTheSameKeyIsHarmless()
	{
		var first = await _session.ToolAsync("create_project",
			new { name = "once", operationKey = "e2e-1" }, CancellationToken);

		var again = await _session.ToolAsync("create_project",
			new { name = "once", operationKey = "e2e-1" }, CancellationToken);

		AreEqual(first.GetProperty("projectId").GetString(), again.GetProperty("projectId").GetString());

		var listed = await _session.ToolAsync("list_projects", new { }, CancellationToken);

		AreEqual(1, listed.GetProperty("projects").GetArrayLength(), "a retry created a second project.");
	}

	/// <summary>Importing data freezes it onto the project and is recorded.</summary>
	[TestMethod]
	public async Task ImportedDataIsFrozenAndRecorded()
	{
		var id = await NewProjectAsync();

		var dataset = await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "e2e-import" }, CancellationToken);

		IsTrue(dataset.GetProperty("isSynthetic").GetBoolean(),
			"generated bars must be marked as generated when they reach the agent.");

		IsTrue(dataset.GetProperty("warning").GetString().Contains("say nothing about any market", StringComparison.Ordinal),
			"the warning about generated data must reach the agent, not only the log.");

		var state = await _session.ToolAsync("get_project_state", new { projectId = id }, CancellationToken);

		AreEqual("Ready", state.GetProperty("status").GetString());

		var trail = await _session.ToolAsync("get_audit_log", new { projectId = id }, CancellationToken);
		var types = trail.GetProperty("events").EnumerateArray()
			.Select(e => e.GetProperty("type").GetString())
			.ToArray();

		CollectionAssert.AreEqual(new[] { "ProjectCreated", "DatasetImported" }, types);
	}

	/// <summary>
	/// The division is described whole and in one piece: three slices end to end, covering exactly the
	/// range the dataset report gives.
	/// </summary>
	[TestMethod]
	public async Task TheSplitIsDescribedWhole()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "e2e-import" }, CancellationToken);

		var split = await _session.ToolAsync("describe_split", new { projectId = id }, CancellationToken);
		var report = await _session.ToolAsync("get_dataset_report", new { projectId = id }, CancellationToken);

		var development = split.GetProperty("development");
		var validation = split.GetProperty("validation");
		var closed = split.GetProperty("closed");

		AreEqual(report.GetProperty("from").GetDateTime(), development.GetProperty("from").GetDateTime());
		AreEqual(development.GetProperty("to").GetDateTime(), validation.GetProperty("from").GetDateTime());
		AreEqual(validation.GetProperty("to").GetDateTime(), closed.GetProperty("from").GetDateTime());
		AreEqual(report.GetProperty("to").GetDateTime(), closed.GetProperty("to").GetDateTime());

		IsFalse(closed.GetProperty("isSpent").GetBoolean(), "the closed slice reads as spent before anything was measured.");
	}

	/// <summary>
	/// The same range divides in the same place in another project, so importing a range again lands on
	/// the slices it was measured on before.
	/// </summary>
	[TestMethod]
	public async Task TheSameRangeDividesTheSameWayInAnotherProject()
	{
		var mine = await NewProjectAsync();
		var yours = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset", new { projectId = mine, operationKey = "e2e-mine" }, CancellationToken);
		await _session.ToolAsync("import_demo_dataset", new { projectId = yours, operationKey = "e2e-yours" }, CancellationToken);

		var first = await _session.ToolAsync("describe_split", new { projectId = mine }, CancellationToken);
		var second = await _session.ToolAsync("describe_split", new { projectId = yours }, CancellationToken);

		AreEqual(
			first.GetProperty("closed").GetProperty("from").GetDateTime(),
			second.GetProperty("closed").GetProperty("from").GetDateTime(),
			"the same range divided differently in two projects.");
	}

	/// <summary>
	/// A refusal has to arrive as something the caller can act on. Told only that something went wrong,
	/// an agent has nothing to aim at and spends its remaining attempts guessing.
	/// </summary>
	[TestMethod]
	public async Task ARefusalArrivesAsSomethingToActOn()
	{
		var refused = await _session.ToolAsync("get_project_state", new { projectId = "not-an-id" }, CancellationToken);

		var error = refused.GetProperty("error");

		AreEqual("InvalidRequest", error.GetProperty("category").GetString());

		IsTrue(error.GetProperty("message").GetString().Contains("prj_", StringComparison.Ordinal),
			"the message must show what a real identifier looks like.");

		IsTrue(error.GetProperty("remediation").GetString().Length > 20,
			"the caller must be told what to do next.");

		IsFalse(string.IsNullOrWhiteSpace(error.GetProperty("correlationId").GetString()),
			"a failure must be traceable to the server logs.");
	}

	/// <summary>Asking about data before importing any names the tool that fixes it.</summary>
	[TestMethod]
	public async Task AskingTooEarlyNamesTheToolThatFixesIt()
	{
		var id = await NewProjectAsync();

		var refused = await _session.ToolAsync("get_dataset_report", new { projectId = id }, CancellationToken);
		var error = refused.GetProperty("error");

		AreEqual("PreconditionFailed", error.GetProperty("category").GetString());

		IsTrue(error.GetProperty("message").GetString().Contains("import_demo_dataset", StringComparison.Ordinal),
			"the refusal must name the tool that resolves it.");
	}

	/// <summary>
	/// The first call an agent makes says what this server is, and everything in the answer is something
	/// the server can back.
	/// </summary>
	[TestMethod]
	public async Task DescribeServerReportsTheProfileItRunsUnder()
	{
		var described = await _session.ToolAsync("describe_server", new { }, CancellationToken);

		AreEqual("Odysseus", described.GetProperty("product").GetString());
		AreEqual("Local", described.GetProperty("mode").GetString());

		IsFalse(string.IsNullOrWhiteSpace(described.GetProperty("version").GetString()));

		foreach (var invented in new[] { "maxResponseBytes", "acceptsHandWrittenSource", "securityAnalyzersEnforced" })
		{
			IsFalse(described.TryGetProperty(invented, out _),
				$"'{invented}' is published again, and nothing in the server acts on it.");
		}
	}

	/// <summary>
	/// The whole point of the specification layer: an agent describes a strategy, the server checks it,
	/// and a bad one is refused with everything that is wrong at once rather than one thing per attempt.
	/// </summary>
	[TestMethod]
	public async Task ASpecificationIsCheckedBeforeItIsRecorded()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "e2e-import" }, CancellationToken);

		var broken = await _session.ToolAsync("validate_spec", new { spec = BrokenSpec }, CancellationToken);

		IsFalse(broken.GetProperty("isValid").GetBoolean());

		var problems = broken.GetProperty("problems").EnumerateArray().ToArray();

		IsTrue(problems.Length >= 2, "everything wrong must be reported together, not one problem per attempt.");

		foreach (var problem in problems)
		{
			IsFalse(string.IsNullOrWhiteSpace(problem.GetProperty("path").GetString()),
				"a problem with no place in the document leaves the caller searching.");

			IsFalse(string.IsNullOrWhiteSpace(problem.GetProperty("remedy").GetString()),
				"a problem with no remedy leaves the caller guessing.");
		}

		var refused = await _session.ToolAsync("propose_spec",
			new { projectId = id, spec = BrokenSpec, operationKey = "e2e-bad" }, CancellationToken);

		AreEqual("SpecValidation", refused.GetProperty("error").GetProperty("category").GetString());

		var listed = await _session.ToolAsync("list_specs", new { projectId = id }, CancellationToken);

		AreEqual(0, listed.GetProperty("specs").GetArrayLength(),
			"a refused specification must not become part of the history: nothing was ever tried with it.");
	}

	/// <summary>A sound specification is recorded, numbered and hashed.</summary>
	[TestMethod]
	public async Task ASoundSpecificationIsRecorded()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "e2e-import" }, CancellationToken);

		var proposed = await _session.ToolAsync("propose_spec",
			new { projectId = id, spec = SoundSpec, operationKey = "e2e-spec" }, CancellationToken);

		AreEqual(1, proposed.GetProperty("revision").GetInt32());
		IsFalse(string.IsNullOrWhiteSpace(proposed.GetProperty("hash").GetString()));
		AreEqual("Agent", proposed.GetProperty("author").GetString());

		var trail = await _session.ToolAsync("get_audit_log", new { projectId = id }, CancellationToken);
		var types = trail.GetProperty("events").EnumerateArray()
			.Select(e => e.GetProperty("type").GetString())
			.ToArray();

		IsTrue(types.Contains("SpecRevised"), "recording a specification must leave a permanent trace.");
	}

	/// <summary>
	/// The whole loop over the wire: a project, data, a specification, a compiled strategy, a run, and
	/// numbers to read. Every part of this is tested on its own elsewhere; what is tested here is that
	/// an agent holding nothing but the tool descriptions can get from one end to the other.
	/// </summary>
	[TestMethod]
	public async Task TheWholeLoopRunsOverTheWire()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "loop-import" }, CancellationToken);

		var proposed = await _session.ToolAsync("propose_spec",
			new { projectId = id, spec = SoundSpec, operationKey = "loop-spec" }, CancellationToken);

		var built = await _session.ToolAsync("build_candidate",
			new
			{
				projectId = id,
				specId = proposed.GetProperty("specId").GetString(),
				operationKey = "loop-build",
			},
			CancellationToken);

		var candidateId = built.GetProperty("candidateId").GetString();

		AreEqual("Compiled", built.GetProperty("status").GetString());
		IsFalse(string.IsNullOrWhiteSpace(built.GetProperty("assemblyHash").GetString()));

		var source = await _session.ToolAsync("get_candidate_source",
			new { projectId = id, candidateId }, CancellationToken);

		var code = source.GetProperty("source").GetString();

		IsTrue(code.Contains(": Strategy", StringComparison.Ordinal),
			"what was built is not a StockSharp strategy.");

		IsTrue(code.Contains("// entries.e1", StringComparison.Ordinal),
			"the generated code cannot be read next to the specification it came from.");

		var run = await _session.ToolAsync("run_backtest",
			new
			{
				projectId = id,
				candidateId,
				slice = "development",
				symbol = "",
				scenario = "",
				parameters = "",
				operationKey = "loop-run",
			},
			CancellationToken);

		AreEqual("Completed", run.GetProperty("status").GetString(),
			$"the run did not finish: {run}");

		IsTrue(run.GetProperty("barsProcessed").GetInt32() > 0, "the strategy saw no bars.");
		IsFalse(run.GetProperty("wasAlreadyRun").GetBoolean());

		// The costs are charged from the first run, so the two sides of the cost line differ.
		AreNotEqual(
			run.GetProperty("gross").GetProperty("profit").GetDecimal(),
			run.GetProperty("net").GetProperty("profit").GetDecimal(),
			"the run was charged nothing at all.");

		var metrics = await _session.ToolAsync("get_metrics",
			new { projectId = id, runId = run.GetProperty("runId").GetString() }, CancellationToken);

		AreEqual(
			run.GetProperty("net").GetProperty("profit").GetDecimal(),
			metrics.GetProperty("net").GetProperty("profit").GetDecimal());

		var trail = await _session.ToolAsync("get_audit_log", new { projectId = id }, CancellationToken);
		var types = trail.GetProperty("events").EnumerateArray()
			.Select(e => e.GetProperty("type").GetString())
			.ToArray();

		IsTrue(types.Contains("CandidateBuilt") && types.Contains("RunCompleted"),
			$"the permanent record holds only {string.Join(", ", types)}.");
	}

	/// <summary>
	/// The same run asked for twice is answered rather than repeated. An agent that loses its connection
	/// mid-run and retries would otherwise pay twice for one measurement.
	/// </summary>
	[TestMethod]
	public async Task RunningTheSameThingTwiceCostsOnce()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "twice-import" }, CancellationToken);

		var proposed = await _session.ToolAsync("propose_spec",
			new { projectId = id, spec = SoundSpec, operationKey = "twice-spec" }, CancellationToken);

		var built = await _session.ToolAsync("build_candidate",
			new { projectId = id, specId = proposed.GetProperty("specId").GetString(), operationKey = "twice-build" },
			CancellationToken);

		var candidateId = built.GetProperty("candidateId").GetString();

		object Request(string key) => new
		{
			projectId = id,
			candidateId,
			slice = "development",
			symbol = "",
			scenario = "",
			parameters = "",
			operationKey = key,
		};

		var first = await _session.ToolAsync("run_backtest", Request("twice-run-a"), CancellationToken);

		// A different key, so the answer cannot come from the operation log: it has to be recognised as
		// the same run by what the run actually was.
		var again = await _session.ToolAsync("run_backtest", Request("twice-run-b"), CancellationToken);

		AreEqual(first.GetProperty("runId").GetString(), again.GetProperty("runId").GetString());
		IsTrue(again.GetProperty("wasAlreadyRun").GetBoolean());

		var state = await _session.ToolAsync("get_project_state", new { projectId = id }, CancellationToken);

		var budget = state.GetProperty("budget");

		AreEqual(
			budget.GetProperty("backtestsTotal").GetInt32() - 1,
			budget.GetProperty("backtestsRemaining").GetInt32(),
			"the repeated run was charged a second time.");
	}

	/// <summary>
	/// The numbers arrive, and only the numbers. What matters here is not which way they went — that
	/// depends on the demo data — but that every slice is reported in full and nothing anywhere says
	/// whether it was good enough, because that is the model's call and not the server's.
	/// </summary>
	[TestMethod]
	public async Task TheMeasurementArrivesAsNumbersAndNoVerdict()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "judge-import" }, CancellationToken);

		var proposed = await _session.ToolAsync("propose_spec",
			new { projectId = id, spec = SoundSpec, operationKey = "judge-spec" }, CancellationToken);

		var built = await _session.ToolAsync("build_candidate",
			new { projectId = id, specId = proposed.GetProperty("specId").GetString(), operationKey = "judge-build" },
			CancellationToken);

		var candidateId = built.GetProperty("candidateId").GetString();

		var measured = await _session.ToolAsync("evaluate_candidate",
			new { projectId = id, candidateId, symbol = "", parameters = "", operationKey = "judge-run" },
			CancellationToken);

		IsTrue(measured.TryGetProperty("measurement", out var m), $"evaluate_candidate answered: {measured}");

		foreach (var slice in new[] { "development", "heldOut", "heldOutStressed" })
		{
			IsTrue(m.TryGetProperty(slice, out var block), $"the {slice} run was not reported: {measured}");

			foreach (var figure in new[] { "netProfit", "maxDrawdownPercent", "trades", "winRatePercent" })
			{
				IsTrue(block.TryGetProperty(figure, out _),
					$"the {slice} run was reported without {figure}.");
			}
		}

		var walkForward = m.GetProperty("walkForward");

		AreEqual(3, walkForward.GetProperty("returnPercentByWindow").GetArrayLength(),
			"the walk-forward windows were not reported one by one.");

		IsTrue(walkForward.TryGetProperty("spread", out _), "the spread between windows was not reported.");

		// Six runs, and the measurement names every one of them.
		AreEqual(6, measured.GetProperty("runs").GetArrayLength());

		// Nothing here decides anything. The words a scoring server would have used are absent.
		var text = measured.ToString();

		foreach (var word in new[] { "verdict", "score", "gate", "threshold", "accepted", "rejected" })
		{
			IsFalse(text.Contains(word, StringComparison.OrdinalIgnoreCase),
				$"the measurement passed judgement by saying '{word}': {text}");
		}

		// Reading it back changes nothing and costs nothing.
		var read = await _session.ToolAsync("get_evaluation",
			new { projectId = id, candidateId }, CancellationToken);

		AreEqual(
			m.GetProperty("heldOut").GetProperty("netProfit").GetDecimal(),
			read.GetProperty("measurement").GetProperty("heldOut").GetProperty("netProfit").GetDecimal());

		IsTrue(read.GetProperty("wasAlreadyMeasured").GetBoolean());
	}

	/// <summary>
	/// The closed part of the history cannot be run as though it were an ordinary slice. Everything the
	/// firewall protects rests on it never having been measured before the one time it is.
	/// </summary>
	[TestMethod]
	public async Task TheClosedSliceCannotBeRunAsAnOrdinaryOne()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "closed-import" }, CancellationToken);

		var proposed = await _session.ToolAsync("propose_spec",
			new { projectId = id, spec = SoundSpec, operationKey = "closed-spec" }, CancellationToken);

		var built = await _session.ToolAsync("build_candidate",
			new { projectId = id, specId = proposed.GetProperty("specId").GetString(), operationKey = "closed-build" },
			CancellationToken);

		var refused = await _session.ToolAsync("run_backtest",
			new
			{
				projectId = id,
				candidateId = built.GetProperty("candidateId").GetString(),
				slice = "final",
				symbol = "",
				scenario = "",
				parameters = "",
				operationKey = "closed-run",
			},
			CancellationToken);

		IsTrue(refused.TryGetProperty("error", out var error), $"the closed slice was run: {refused}");
		IsTrue(error.GetProperty("message").GetString().Contains("once", StringComparison.Ordinal),
			$"the refusal does not say why: {error}");
	}

	/// <summary>
	/// The closed part of the history is spent once. Everything the earlier numbers are worth rests on
	/// that, so the second attempt on the same candidate is refused rather than quietly repeated.
	/// </summary>
	[TestMethod]
	public async Task TheClosedSliceIsSpentOnceAndOnlyOnce()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "final-import" }, CancellationToken);

		var proposed = await _session.ToolAsync("propose_spec",
			new { projectId = id, spec = SoundSpec, operationKey = "final-spec" }, CancellationToken);

		var built = await _session.ToolAsync("build_candidate",
			new { projectId = id, specId = proposed.GetProperty("specId").GetString(), operationKey = "final-build" },
			CancellationToken);

		var candidateId = built.GetProperty("candidateId").GetString();

		var untouched = await _session.ToolAsync("get_closed_data_state", new { projectId = id }, CancellationToken);

		IsFalse(untouched.GetProperty("spent").GetBoolean());

		// The same fact, asked of the data rather than of the runs, has to agree with itself.
		var before = await _session.ToolAsync("describe_split", new { projectId = id }, CancellationToken);

		IsFalse(before.GetProperty("closed").GetProperty("isSpent").GetBoolean(),
			"the split says the closed slice is spent before anything has been measured on it.");

		// Before the candidate has been measured on the open data there is nothing to compare against, and
		// the slice stays closed.
		var tooEarly = await _session.ToolAsync("measure_on_closed_data",
			new { projectId = id, candidateId, operationKey = "final-early" },
			CancellationToken);

		IsTrue(tooEarly.TryGetProperty("error", out _), $"an unmeasured candidate reached the closed slice: {tooEarly}");

		await _session.ToolAsync("evaluate_candidate",
			new { projectId = id, candidateId, symbol = "", parameters = "", operationKey = "final-judge" },
			CancellationToken);

		var confirmed = await _session.ToolAsync("measure_on_closed_data",
			new { projectId = id, candidateId, operationKey = "final-check" },
			CancellationToken);

		IsTrue(confirmed.TryGetProperty("onClosedData", out var closed), $"the closed data answered: {confirmed}");
		IsTrue(closed.TryGetProperty("heldOut", out _), "the closed measurement is missing its runs.");

		IsTrue(confirmed.TryGetProperty("onOpenData", out _),
			"what the candidate did on the open data was not returned beside it.");

		AreEqual(0, confirmed.GetProperty("provenance").GetProperty("earlierCandidatesThatSpentTheSlice").GetInt32());

		IsTrue(confirmed.GetProperty("note").GetString().Contains("spent", StringComparison.Ordinal),
			"the report does not say that the closed slice has been used up.");

		var spent = await _session.ToolAsync("get_closed_data_state", new { projectId = id }, CancellationToken);

		IsTrue(spent.GetProperty("spent").GetBoolean());

		var after = await _session.ToolAsync("describe_split", new { projectId = id }, CancellationToken);

		IsTrue(after.GetProperty("closed").GetProperty("isSpent").GetBoolean(),
			"the split still says the closed slice is untouched after it was measured.");
		AreEqual(candidateId, spent.GetProperty("measuredCandidates")[0].GetString());

		var again = await _session.ToolAsync("measure_on_closed_data",
			new { projectId = id, candidateId, operationKey = "final-again" },
			CancellationToken);

		IsTrue(again.TryGetProperty("error", out var error), $"the same candidate was measured twice: {again}");
		IsTrue(error.GetProperty("message").GetString().Contains("already", StringComparison.Ordinal));

		var listed = await _session.ToolAsync("list_candidates", new { projectId = id }, CancellationToken);

		var status = listed.GetProperty("candidates")[0].GetProperty("status").GetString();

		AreEqual("FinalChecked", status,
			"the candidate was left at the wrong stage after the closed data was measured.");
	}

	/// <summary>The catalog is the vocabulary, and an unknown name is refused against it by suggestion.</summary>
	[TestMethod]
	public async Task TheCatalogIsTheVocabulary()
	{
		var catalog = await _session.ToolAsync("get_indicator_catalog", new { }, CancellationToken);
		var names = catalog.GetProperty("indicators").EnumerateArray()
			.Select(i => i.GetProperty("name").GetString())
			.ToArray();

		IsTrue(names.Contains("sma") && names.Contains("atr"), $"the catalog published {string.Join(", ", names)}.");

		var misspelled = SoundSpec.Replace("\"highest\"", "\"highst\"", StringComparison.Ordinal);
		var checkResult = await _session.ToolAsync("validate_spec", new { spec = misspelled }, CancellationToken);

		var remedy = checkResult.GetProperty("problems")[0].GetProperty("remedy").GetString();

		IsTrue(remedy.Contains("highest", StringComparison.Ordinal),
			$"a misspelled indicator must be answered with the nearest real name; the remedy said: {remedy}");
	}

	/// <summary>
	/// The whole path, ending where the product ends: the model reads the numbers, decides, and the
	/// server writes down what was decided along with everything needed to check it later.
	/// </summary>
	/// <remarks>
	/// This is the one conclusion in the product and it arrives from outside. What is asserted here is
	/// that the server keeps it whole — the code, the specification, the instrument, the data it was
	/// measured on and every run — because a folder holding a headline figure is a strategy nobody can
	/// check.
	/// </remarks>
	[TestMethod]
	public async Task AStrategyIsFinishedByTheModelAndKeptByTheServer()
	{
		var id = await NewProjectAsync();

		var imported = await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "done-import" }, CancellationToken);

		var proposed = await _session.ToolAsync("propose_spec",
			new { projectId = id, spec = SoundSpec, operationKey = "done-spec" }, CancellationToken);

		var built = await _session.ToolAsync("build_candidate",
			new { projectId = id, specId = proposed.GetProperty("specId").GetString(), operationKey = "done-build" },
			CancellationToken);

		var candidateId = built.GetProperty("candidateId").GetString();

		// Nothing has been measured on the closed data, so there is nothing to conclude from yet.
		var tooEarly = await _session.ToolAsync("complete_strategy",
			new { projectId = id, candidateId, notes = "Looks good." }, CancellationToken);

		IsTrue(tooEarly.TryGetProperty("error", out _), $"a candidate was finished on no evidence: {tooEarly}");

		await _session.ToolAsync("evaluate_candidate",
			new { projectId = id, candidateId, symbol = "", parameters = "", operationKey = "done-measure" },
			CancellationToken);

		await _session.ToolAsync("measure_on_closed_data",
			new { projectId = id, candidateId, operationKey = "done-closed" }, CancellationToken);

		var finished = await _session.ToolAsync("complete_strategy",
			new
			{
				projectId = id,
				candidateId,
				notes = "Held its shape on the closed slice and traded often enough to believe.",
			},
			CancellationToken);

		AreEqual("Completed", finished.GetProperty("status").GetString());

		var kept = finished.GetProperty("kept");

		IsFalse(string.IsNullOrWhiteSpace(kept.GetProperty("symbol").GetString()),
			"the instrument it was measured on was not kept.");

		IsTrue(kept.GetProperty("dataset").GetProperty("to").GetDateTime() > kept.GetProperty("dataset").GetProperty("from").GetDateTime(),
			"the range it was measured on was not kept.");

		IsTrue(kept.GetProperty("runs").GetArrayLength() >= 7,
			"the runs behind the result were not kept.");

		IsTrue(finished.TryGetProperty("onOpenData", out _) && finished.TryGetProperty("onClosedData", out _),
			"the two measurements were not returned side by side.");

		// The folder is a real one, holding files that can be opened without this server.
		var folder = finished.GetProperty("folder").GetString();

		IsTrue(Directory.Exists(folder), $"nothing was written to {folder}.");
		IsTrue(File.Exists(Path.Combine(folder, "strategy.json")), "the metadata file is missing.");
		IsTrue(File.Exists(Path.Combine(folder, "spec.json")), "the specification is missing.");

		IsTrue(Directory.EnumerateFiles(folder, "*.cs").Any(), "the strategy source is missing.");

		var listed = await _session.ToolAsync("list_completed_strategies", new { projectId = id }, CancellationToken);

		AreEqual(1, listed.GetProperty("completed").GetArrayLength());

		AreEqual(candidateId, listed.GetProperty("completed")[0].GetProperty("candidateId").GetString());

		// Concluding it twice would rewrite the conclusion.
		var again = await _session.ToolAsync("complete_strategy",
			new { projectId = id, candidateId, notes = "Still good." }, CancellationToken);

		IsTrue(again.TryGetProperty("error", out _), $"the same strategy was finished twice: {again}");
	}

	/// <summary>
	/// Measuring history that is already imported needs no account, and the server must not pretend
	/// otherwise.
	/// </summary>
	/// <remarks>
	/// analyze_market reads bars off the disk and computes over them. It happens to live on the service
	/// that also downloads, and that accident is not a reason to refuse it on a machine with no keys -
	/// which is most machines trying the product out.
	/// </remarks>
	[TestMethod]
	public async Task MeasuringFrozenDataNeedsNoAccount()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "nokeys-import" }, CancellationToken);

		var measured = await _session.ToolAsync("analyze_market",
			new { projectId = id, symbol = "" }, CancellationToken);

		IsFalse(measured.TryGetProperty("error", out var refused),
			$"measuring imported data was refused without an account: {refused}");

		IsTrue(measured.GetProperty("coverage").GetProperty("bars").GetInt32() > 0,
			$"nothing was measured: {measured}");
	}

	/// <summary>
	/// What genuinely needs an account says so, in the published error shape, naming what is missing and
	/// what still works without it.
	/// </summary>
	/// <remarks>
	/// The failure mode this replaces was the worst kind: the tool's arguments could not be bound, so no
	/// code of ours ran, and the agent received the transport's own wording - that something went wrong
	/// invoking a tool. No category to branch on, no mention of credentials, nothing to do next.
	/// </remarks>
	[TestMethod]
	public async Task WhatNeedsAnAccountSaysSoAndSaysWhatStillWorks()
	{
		var id = await NewProjectAsync();

		var refused = await _session.ToolAsync("import_history",
			new { projectId = id, symbols = "NVDA", timeFrame = "5m", days = 5, operationKey = "nokeys-download" },
			CancellationToken);

		IsTrue(refused.TryGetProperty("error", out var error),
			$"downloading without an account was not refused: {refused}");

		AreEqual("ConfigurationInvalid", error.GetProperty("category").GetString());

		IsTrue(error.GetProperty("message").GetString().Contains("broker account", StringComparison.Ordinal),
			$"the refusal does not say what is missing: {error}");

		var remediation = error.GetProperty("remediation").GetString();

		IsTrue(remediation.Contains("ODYSSEUS_BROKER_CONNECTOR", StringComparison.Ordinal),
			$"the refusal does not say how to supply an account: {remediation}");

		IsTrue(remediation.Contains("import_demo_dataset", StringComparison.Ordinal),
			$"the refusal does not say what can be done without one: {remediation}");
	}

	/// <summary>
	/// Reading what has been deployed works without an account: the answer is that nothing has, and a
	/// question about our own records never needed a broker to answer.
	/// </summary>
	[TestMethod]
	public async Task ReadingDeploymentsNeedsNoAccount()
	{
		var id = await NewProjectAsync();

		var listed = await _session.ToolAsync("list_deployments", new { projectId = id }, CancellationToken);

		IsFalse(listed.TryGetProperty("error", out var refused),
			$"listing our own records was refused without an account: {refused}");

		AreEqual(0, listed.GetProperty("deployments").GetArrayLength());
	}

	/// <summary>
	/// Reading the account is a tool, and on a server with no account it refuses like every other one.
	/// </summary>
	/// <remarks>
	/// It exists because for a long time nothing could answer the question. Every figure an agent could
	/// see about a deployment came from what this product recorded while it was watching, so one left by
	/// a server that died read as flat forever - and the tools told the caller to go and check the
	/// account, with nothing to check it with.
	/// </remarks>
	[TestMethod]
	public async Task ReadingTheAccountIsPublishedAndRefusesWithoutOne()
	{
		var tools = await _session.ListToolsAsync(CancellationToken);

		IsTrue(tools.Contains("get_account_state"),
			$"the account cannot be read at all; the server published {string.Join(", ", tools)}.");

		// An empty deployment reads this server's own account, which is the connector it bound. Naming a
		// deployment reads through that deployment's own runner instead, and the two need not be the same
		// account - which is why the old name, which claimed one of them, had to go.
		var refused = await _session.ToolAsync("get_account_state", new { deploymentId = "" }, CancellationToken);

		IsTrue(refused.TryGetProperty("error", out var error),
			$"an account was reported on a server that has none: {refused}");

		AreEqual("ConfigurationInvalid", error.GetProperty("category").GetString());
	}

	/// <summary>
	/// A server with no installer on the machine answers the question about installing, and refuses the
	/// acts, in the published error shape.
	/// </summary>
	/// <remarks>
	/// This is the whole of the product half as almost every machine will meet it: the StockSharp
	/// installer is not there, this server never fetches one, and no operator has said a product may be
	/// installed. Both halves of the answer matter. Reading the state has to work - it is what an agent
	/// calls before planning around any of this - and the five that act have to refuse with a category
	/// to branch on and a message naming what an operator would have to supply, rather than failing
	/// while their arguments are bound.
	/// </remarks>
	[TestMethod]
	public async Task WithNoInstallerTheStateIsReadableAndTheRestIsRefused()
	{
		var state = await _session.ToolAsync("get_installer_state", new { }, CancellationToken);

		IsFalse(state.TryGetProperty("error", out var refusedState),
			$"asking what can be installed was refused rather than answered: {refusedState}");

		IsFalse(state.GetProperty("available").GetBoolean(),
			$"a server with no installer reported that it could install: {state}");

		IsTrue(state.GetProperty("lookedIn").GetArrayLength() > 0,
			$"the answer does not say where the installer was looked for: {state}");

		AreEqual(0, state.GetProperty("allowedProducts").GetArrayLength(),
			$"a server nobody configured allowed a product: {state}");

		IsTrue(state.GetProperty("whatThisMeans").GetString().Contains("never downloads", StringComparison.Ordinal),
			$"the answer does not say that this server never fetches the installer: {state}");

		foreach (var (tool, arguments) in new (string, object)[]
		{
			("list_products", new { }),
			("list_installed_products", new { }),
			("install_product", new { productId = 9, reinstall = false }),
			("update_product", new { productId = 9, backupSettings = false }),
			("remove_product", new { productId = 9, removeData = false }),
		})
		{
			var refused = await _session.ToolAsync(tool, arguments, CancellationToken);

			IsTrue(refused.TryGetProperty("error", out var error),
				$"{tool} answered on a machine with no installer: {refused}");

			AreEqual("ConfigurationInvalid", error.GetProperty("category").GetString(), tool);

			IsTrue(error.GetProperty("remediation").GetString().Contains("ODYSSEUS_INSTALLER", StringComparison.Ordinal),
				$"{tool} does not say what an operator would have to supply: {error}");
		}

		var described = await _session.ToolAsync("describe_server", new { }, CancellationToken);

		IsFalse(described.GetProperty("canInstallProducts").GetBoolean(),
			$"describe_server claimed products could be installed: {described}");
	}

	/// <summary>
	/// Every argument a tool publishes is one the caller is expected to supply, and one it can only
	/// supply if it is told what the argument is.
	/// </summary>
	/// <remarks>
	/// This is the shape of a defect that shipped. A service the server had not registered stopped being
	/// resolved from the container and became an argument for the model to fill: a required property
	/// named 'service', of type object, with no description - because nobody wrote one, since nobody
	/// meant to publish it. Its own contract said so, and nothing was reading it. Every argument written
	/// on purpose here carries a description, so the absence of one is the signature of an argument
	/// nobody meant to publish.
	/// </remarks>
	[TestMethod]
	public async Task EveryPublishedArgumentSaysWhatItIs()
	{
		var described = await _session.CallAsync("tools/list", new { }, CancellationToken);

		foreach (var tool in described.GetProperty("tools").EnumerateArray())
		{
			var name = tool.GetProperty("name").GetString();

			if (!tool.TryGetProperty("inputSchema", out var schema) ||
				!schema.TryGetProperty("properties", out var properties))
			{
				continue;
			}

			foreach (var argument in properties.EnumerateObject())
			{
				IsTrue(argument.Value.TryGetProperty("description", out var text) &&
					!string.IsNullOrWhiteSpace(text.GetString()),
					$"'{name}' publishes an argument '{argument.Name}' without saying what it is, which is " +
					"what an argument nobody meant to publish looks like.");
			}
		}
	}

	/// <summary>The server says which half of itself is available before anything is attempted.</summary>
	/// <remarks>
	/// The list is checked name by name, because a list that is only mostly right is worse than none:
	/// an agent that planned around it is refused mid-plan by the tool the list left out, which is the
	/// discovery this tool exists to prevent.
	/// </remarks>
	[TestMethod]
	public async Task TheServerSaysWhetherItHasAnAccount()
	{
		var described = await _session.ToolAsync("describe_server", new { }, CancellationToken);

		IsFalse(described.GetProperty("hasBroker").GetBoolean(),
			"a server started without credentials reported that it has an account.");

		var needed = described.GetProperty("whatNeedsABroker").GetString();

		foreach (var name in new[]
		{
			"import_history",
			"lookup_symbols",
			"lookup_option_contracts",
			"deploy_candidate",
			"get_account_state",
		})
		{
			IsTrue(needed.Contains(name, StringComparison.Ordinal),
				$"'{name}' is refused without an account and the server does not say so: {needed}");
		}
	}

	/// <summary>
	/// A server says plainly that what it starts keeps trading after the session ends, and that nothing
	/// reachable from here trades real money.
	/// </summary>
	/// <remarks>
	/// Both halves are the answer to a question an agent cannot check for itself. The first replaces the
	/// opposite claim - deployments used to die with the process - and getting it wrong in that direction
	/// leaves strategies running that somebody believes were flattened by a disconnect. The second is a
	/// property rather than a procedure: there is nothing here to change, so there is nothing here that
	/// could be read out as a recipe for changing it.
	/// </remarks>
	[TestMethod]
	public async Task TheServerSaysWhatOutlivesTheSessionAndWhatItWillNotDo()
	{
		var described = await _session.ToolAsync("describe_server", new { }, CancellationToken);

		IsFalse(described.GetProperty("liveTradingReachableFromHere").GetBoolean(),
			"a server reachable over this channel reported that it can trade real money.");

		var registry = described.GetProperty("runnerRegistry").GetString();

		IsTrue(registry is { Length: > 0 },
			"the server does not say where the deployments it starts keep their records.");

		var outlives = described.GetProperty("whatHappensWhenYouDisconnect").GetString();

		IsTrue(outlives.Contains("keep trading", StringComparison.OrdinalIgnoreCase),
			$"the server does not say that a deployment outlives the session: {outlives}");
	}

	/// <summary>
	/// A long list arrives a page at a time, and the answer says how to ask for the rest.
	/// </summary>
	/// <remarks>
	/// Everything used to come back at once. A project may hold hundreds of runs and a permanent record
	/// longer than that, and an agent handed all of it spends its attention on scrolling rather than on
	/// the entries that matter - while a client with a message limit gets nothing at all.
	/// </remarks>
	[TestMethod]
	public async Task ALongListArrivesAPageAtATime()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "page-import" }, CancellationToken);

		// A handful of revisions, so there is something to page through.
		for (var i = 0; i < 5; i++)
		{
			await _session.ToolAsync("propose_spec",
				new
				{
					projectId = id,
					spec = SoundSpec.Replace("Volume confirmed breakout", $"Volume confirmed breakout {i}", StringComparison.Ordinal),
					operationKey = $"page-spec-{i}",
				},
				CancellationToken);
		}

		var first = await _session.ToolAsync("list_specs",
			new { projectId = id, offset = 0, limit = 2 }, CancellationToken);

		AreEqual(2, first.GetProperty("specs").GetArrayLength());

		var window = first.GetProperty("window");

		AreEqual(5, window.GetProperty("total").GetInt32());
		AreEqual(2, window.GetProperty("returned").GetInt32());
		IsTrue(window.GetProperty("hasMore").GetBoolean());
		AreEqual(2, window.GetProperty("nextOffset").GetInt32());

		var second = await _session.ToolAsync("list_specs",
			new { projectId = id, offset = window.GetProperty("nextOffset").GetInt32(), limit = 2 },
			CancellationToken);

		AreNotEqual(
			first.GetProperty("specs")[0].GetProperty("specId").GetString(),
			second.GetProperty("specs")[0].GetProperty("specId").GetString(),
			"the second page repeats the first.");

		var last = await _session.ToolAsync("list_specs",
			new { projectId = id, offset = 4, limit = 50 }, CancellationToken);

		IsFalse(last.GetProperty("window").GetProperty("hasMore").GetBoolean());
		IsTrue(!last.GetProperty("window").TryGetProperty("nextOffset", out var end) ||
			end.ValueKind == JsonValueKind.Null,
			"the last page still points at a next one.");

		// Asking for nothing in particular is the first page, so nothing has to know about paging to read.
		var plain = await _session.ToolAsync("list_specs", new { projectId = id }, CancellationToken);

		AreEqual(5, plain.GetProperty("specs").GetArrayLength());
	}

	/// <summary>
	/// There is a way to find out what can be traded, including a contract nobody could have guessed.
	/// </summary>
	/// <remarks>
	/// The product handled an option contract correctly all along - the hundred shares it carries are in
	/// every figure - and gave nobody a way to name one. A share can be guessed; a contract code carries
	/// the underlying, the expiry, the right and the strike in nineteen characters that have to be
	/// exactly right.
	/// </remarks>
	[TestMethod]
	public async Task ThereIsAWayToFindOutWhatCanBeTraded()
	{
		var tools = await _session.ListToolsAsync(CancellationToken);

		IsTrue(tools.Contains("lookup_symbols"), "nothing says what instruments exist.");
		IsTrue(tools.Contains("lookup_option_contracts"), "nothing says what contracts exist.");

		// Without an account there is nobody to ask, and that is answered rather than guessed at.
		foreach (var (name, arguments) in new (string, object)[]
		{
			("lookup_symbols", new { query = "NVDA" }),
			("lookup_option_contracts", new { underlying = "NVDA" }),
		})
		{
			var refused = await _session.ToolAsync(name, arguments, CancellationToken);

			IsTrue(refused.TryGetProperty("error", out var error), $"{name} answered without an account: {refused}");
			AreEqual("ConfigurationInvalid", error.GetProperty("category").GetString());
		}
	}

	/// <summary>
	/// A run trades one instrument, and says which of the dataset it left alone.
	/// </summary>
	/// <remarks>
	/// The choice is made for the caller when it does not name a symbol - it gets the first of the
	/// dataset - so a two-symbol import measured on one of them looks exactly like a one-symbol import.
	/// Combining instruments into one result is a different product; saying what was not covered is what
	/// this one owes the reader.
	/// </remarks>
	[TestMethod]
	public async Task ARunSaysWhichSymbolsItDidNotCover()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "cover-import" }, CancellationToken);

		var report = await _session.ToolAsync("get_dataset_report", new { projectId = id }, CancellationToken);

		var symbols = report.GetProperty("symbols").EnumerateArray().Select(s => s.GetString()).ToArray();

		IsTrue(symbols.Length > 1, $"the bundled dataset holds {symbols.Length} symbol(s), so this proves nothing.");

		var proposed = await _session.ToolAsync("propose_spec",
			new { projectId = id, spec = SoundSpec, operationKey = "cover-spec" }, CancellationToken);

		var built = await _session.ToolAsync("build_candidate",
			new { projectId = id, specId = proposed.GetProperty("specId").GetString(), operationKey = "cover-build" },
			CancellationToken);

		var run = await _session.ToolAsync("run_backtest",
			new
			{
				projectId = id,
				candidateId = built.GetProperty("candidateId").GetString(),
				slice = "Development",
				symbol = "",
				scenario = "",
				parameters = "",
				operationKey = "cover-run",
			},
			CancellationToken);

		IsTrue(run.TryGetProperty("symbolsNotCovered", out var untouched),
			$"a run over one of several symbols did not say what it left out: {run}");

		var left = untouched.EnumerateArray().Select(s => s.GetString()).ToArray();

		AreEqual(symbols.Length - 1, left.Length);
		IsFalse(left.Contains(run.GetProperty("symbol").GetString()), "the symbol it ran on is listed as uncovered.");
	}

	/// <summary>
	/// A part that traded nothing says so, and says why.
	/// </summary>
	/// <remarks>
	/// This is the whole product in one assertion. No trades and no edge are the same row of zeros and
	/// opposite findings: one says the hypothesis did not describe anything that happened there, the
	/// other says it did and the market paid nothing for it. The run has always worked out which - and
	/// the measurement, which is the tool the whole workflow turns on, used to drop the sentence and
	/// report the zeros.
	/// </remarks>
	[TestMethod]
	public async Task APartThatTradedNothingSaysWhy()
	{
		var id = await NewProjectAsync();

		await _session.ToolAsync("import_demo_dataset",
			new { projectId = id, operationKey = "silent-import" }, CancellationToken);

		var proposed = await _session.ToolAsync("propose_spec",
			new { projectId = id, spec = SoundSpec, operationKey = "silent-spec" }, CancellationToken);

		var built = await _session.ToolAsync("build_candidate",
			new { projectId = id, specId = proposed.GetProperty("specId").GetString(), operationKey = "silent-build" },
			CancellationToken);

		var measured = await _session.ToolAsync("evaluate_candidate",
			new
			{
				projectId = id,
				candidateId = built.GetProperty("candidateId").GetString(),
				symbol = "",
				parameters = "",
				operationKey = "silent-measure",
			},
			CancellationToken);

		var measurement = measured.GetProperty("measurement");

		var silent = new List<string>();

		foreach (var part in new[] { "development", "heldOut", "heldOutStressed" })
		{
			if (measurement.GetProperty(part).GetProperty("trades").GetInt32() == 0)
				silent.Add(part);
		}

		if (silent.Count == 0)
			return;

		IsTrue(measurement.TryGetProperty("whyNothingHappened", out var reasons) &&
			reasons.ValueKind == JsonValueKind.Array,
			$"{string.Join(" and ", silent)} traded nothing and the measurement gave no reason: {measurement}");

		var named = reasons.EnumerateArray().Select(r => r.GetProperty("slice").GetString()).ToArray();

		foreach (var part in silent)
			IsTrue(named.Contains(part), $"'{part}' traded nothing and is not among the reasons given.");

		foreach (var reason in reasons.EnumerateArray())
		{
			IsFalse(string.IsNullOrWhiteSpace(reason.GetProperty("why").GetString()),
				"a part was named as silent with no explanation, which is the blank this exists to prevent.");
		}
	}

	private async Task<string> NewProjectAsync()
	{
		var created = await _session.ToolAsync("create_project",
			new { name = "study", operationKey = Guid.NewGuid().ToString("n") }, CancellationToken);

		return created.GetProperty("projectId").GetString();
	}
}
