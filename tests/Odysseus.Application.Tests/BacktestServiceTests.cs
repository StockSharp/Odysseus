namespace StockSharp.Odysseus.Application.Tests;

using System.Threading;

using StockSharp.Odysseus.Engine;
using StockSharp.Odysseus.Persistence;
using StockSharp.Odysseus.Platform;
using StockSharp.Odysseus.Spec;

/// <summary>
/// What a run costs, and what it costs when it does not happen.
/// </summary>
/// <remarks>
/// The allowance is the only thing standing between a search and an unbounded one, so it has to be
/// counted exactly: charged for work that was done, and not charged for work that was abandoned. A
/// client that drops a connection is ordinary - it happens on a timeout, on a restart, on a person
/// pressing a key - and if each of those quietly costs a backtest, the ceiling arrives sooner than the
/// count of results explains and nobody can say why.
/// </remarks>
[TestClass]
public class BacktestServiceTests : OdysseusTestBase
{
	private static readonly DateTime _open = new(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc);

	private string _root;
	private SqliteProjectStore _store;
	private SqliteOperationLog _operations;
	private FileArtifactStore _artifacts;
	private Store _results;
	private FileDatasetStore _datasets;
	private FileSpecStore _specs;
	private Runner _runner;
	private ProjectService _projects;
	private CandidateService _candidates;
	private BacktestService _service;

	/// <summary>Builds the services over temporary storage.</summary>
	[TestInitialize]
	public void CreateService()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new SqliteProjectStore(_root);
		_operations = new SqliteOperationLog(_root);
		_artifacts = new FileArtifactStore(_root);
		_datasets = new FileDatasetStore(_root, new LocalBarStorage(Path.Combine(_root, "market-data")));
		_specs = new FileSpecStore(_root);
		_results = new Store(_artifacts);
		_runner = new Runner();

		var clock = new SystemClock();
		var budget = new ResearchBudget(20, 5, TimeSpan.FromMinutes(45)).ToState();

		_projects = new ProjectService(_store, _store, _operations, clock, ServerModes.Local, budget);
		_candidates = new CandidateService(_store, _specs, _store, _artifacts, new Builder(), _store, _operations, clock);

		_service = new BacktestService(
			_store, _store, _specs, _datasets, _store, _results, _runner, new StockSharpMarketProfiler(), _store, _operations, clock);
	}

	/// <summary>Releases storage.</summary>
	[TestCleanup]
	public void DeleteService()
	{
		_store?.Dispose();
		_operations?.Dispose();

		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>A run that finished is charged once.</summary>
	[TestMethod]
	public async Task AFinishedRunCostsOne()
	{
		var (project, candidate) = await ReadyAsync();

		var before = await RemainingAsync(project);

		await _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		AreEqual(before - 1, await RemainingAsync(project));
	}

	/// <summary>
	/// A run the caller gave up on costs nothing, because nothing was measured.
	/// </summary>
	/// <remarks>
	/// The allowance is claimed before the work, which is right - two runs arriving together must cost
	/// two - but a claim taken for work that never happened has to be given back, or a dropped connection
	/// is a silent charge and the ceiling arrives early for no reason anyone can see.
	/// </remarks>
	[TestMethod]
	public async Task AnAbandonedRunCostsNothing()
	{
		var (project, candidate) = await ReadyAsync();

		var before = await RemainingAsync(project);

		_runner.Cancel = true;

		await ThrowsAsync<OperationCanceledException>(() => _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken).AsTask());

		AreEqual(before, await RemainingAsync(project),
			"the abandoned run was charged to the allowance.");
	}

	/// <summary>
	/// Asking again after giving up costs one, not two: the abandoned attempt left nothing behind, so
	/// the retry is the first run of that measurement rather than the second.
	/// </summary>
	[TestMethod]
	public async Task RetryingAfterGivingUpCostsOne()
	{
		var (project, candidate) = await ReadyAsync();

		var before = await RemainingAsync(project);
		var key = Guid.NewGuid().ToString("n");

		_runner.Cancel = true;

		await ThrowsAsync<OperationCanceledException>(() => _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, key, Actors.Agent, CancellationToken).AsTask());

		_runner.Cancel = false;

		await _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, key, Actors.Agent, CancellationToken);

		AreEqual(before - 1, await RemainingAsync(project),
			"the measurement cost two backtests although only one of them ran.");
	}

	/// <summary>A run that failed for a reason of its own is charged, because it is a result.</summary>
	/// <remarks>
	/// This is the line the release must not cross. A candidate that cannot be run has been answered,
	/// and the attempt is evidence; only work the caller walked away from is given back.
	/// </remarks>
	[TestMethod]
	public async Task ARunThatFailedIsStillCharged()
	{
		var (project, candidate) = await ReadyAsync();

		var before = await RemainingAsync(project);

		_runner.Fail = true;

		var (run, _) = await _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		AreEqual(RunStatuses.Failed, run.Status);
		AreEqual(before - 1, await RemainingAsync(project), "a failed run is a result and is charged.");
	}

	/// <summary>
	/// A run the worker had to stop is the candidate's own failure: recorded and charged like any other
	/// attempt, and then re-raised so the caller learns which of the two happened.
	/// </summary>
	/// <remarks>
	/// A deadline and a memory limit are on this side of the line, and it matters that they are. What
	/// overran is the candidate's own arithmetic - the machinery did precisely what it exists for - so
	/// this is a finding about the strategy, and the error contract answers it with a remediation
	/// addressed to the specification. Recorded, because the attempt happened and hiding it would let the
	/// same one be proposed again.
	/// </remarks>
	/// <param name="kind">Which limit the run reached.</param>
	[TestMethod]
	[DataRow(IsolationFailures.Timeout)]
	[DataRow(IsolationFailures.Memory)]
	public async Task ARunTheWorkerWasStoppedInIsRecordedAndThenReported(IsolationFailures kind)
	{
		var (project, candidate) = await ReadyAsync();

		var before = await RemainingAsync(project);
		var key = Guid.NewGuid().ToString("n");

		_runner.Isolation = kind;

		var stopped = await ThrowsAsync<IsolationFailedException>(async () => await _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, key, Actors.Agent, CancellationToken));

		AreEqual(kind, stopped.Kind);
		AreEqual(before - 1, await RemainingAsync(project), "a run the worker was stopped in is still an attempt.");

		_runner.Isolation = null;

		// Recorded, which is what the operation key proves: repeating the call returns the attempt rather
		// than running a second one.
		var (run, wasAlreadyRun) = await _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, key, Actors.Agent, CancellationToken);

		IsTrue(wasAlreadyRun, "the attempt the worker was stopped in was not recorded.");
		AreEqual(RunStatuses.Failed, run.Status);
	}

	/// <summary>
	/// A worker that died mid-sentence, or one this server cannot speak to, is not the candidate's
	/// failure: the attempt is recorded as interrupted, the allowance is untouched, and the caller is
	/// told to ask again rather than handed a result about a strategy that was never asked anything.
	/// </summary>
	/// <remarks>
	/// This is the half of the line that used to be missing. Recorded as a failure and charged, a flaky
	/// machine spent a finite allowance and left a record in which a defect in a hypothesis and a defect
	/// in this server look exactly alike - and the candidate, having been "answered", could not be
	/// proposed again to find out which it had been.
	/// </remarks>
	/// <param name="kind">How the worker failed.</param>
	[TestMethod]
	[DataRow(IsolationFailures.Crashed)]
	[DataRow(IsolationFailures.Handshake)]
	public async Task AWorkerThatFailedIsNotTheCandidatesFailure(IsolationFailures kind)
	{
		var (project, candidate) = await ReadyAsync();

		var before = await RemainingAsync(project);
		var key = Guid.NewGuid().ToString("n");

		_runner.Isolation = kind;

		var broken = await ThrowsAsync<HarnessFailedException>(async () => await _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, key, Actors.Agent, CancellationToken));

		AreEqual(HarnessFailures.Worker, broken.Kind);

		IsTrue(broken.Message.Contains("worker", StringComparison.OrdinalIgnoreCase),
			$"the caller must be told what broke: {broken.Message}");

		AreEqual(before, await RemainingAsync(project),
			"a failure of this server's own machinery was charged to the research allowance.");

		var recorded = await _store.ListAsync(project, candidate, CancellationToken);

		AreEqual(1, recorded.Count, "the attempt was hidden rather than recorded.");
		AreEqual(RunStatuses.Interrupted, recorded[0].Status,
			"an attempt the machinery broke was recorded as the candidate having failed.");

		_runner.Isolation = null;

		// The operation key was not taken, so asking again with it runs the measurement rather than
		// handing back the attempt that never measured anything.
		var (run, wasAlreadyRun) = await _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, key, Actors.Agent, CancellationToken);

		IsFalse(wasAlreadyRun, "the retry was answered with the interrupted attempt instead of running.");
		AreEqual(RunStatuses.Completed, run.Status);
		AreEqual(before - 1, await RemainingAsync(project), "only the run that measured something was charged.");
	}

	/// <summary>
	/// A worker binary that was never installed is this server's failure too, and is not charged.
	/// </summary>
	/// <remarks>
	/// The other worker failures reach this service already typed, because the fake runner raises them
	/// that way. This one comes the whole distance through the real host: the worker path points at
	/// nothing, which is the most ordinary deployment mistake there is, and the operating system's error
	/// for it is an ordinary exception raised while a candidate was being run - indistinguishable from
	/// the candidate throwing. Told apart nowhere else, the allowance is spent, the strategy is recorded
	/// as having failed something it was never asked, and the operation key is consumed by a run that
	/// never started.
	/// </remarks>
	[TestMethod]
	public async Task AWorkerThatWasNeverInstalledIsNotTheCandidatesFailure()
	{
		var (project, candidate) = await ReadyAsync();

		var before = await RemainingAsync(project);
		var key = Guid.NewGuid().ToString("n");

		var missing = Path.Combine(Path.GetTempPath(), $"odysseus-worker-{Guid.NewGuid():n}.exe");

		using var host = new WorkerHost(
			new WorkerOptions(
				missing,
				Arguments: [],
				HandshakeDeadline: TimeSpan.FromSeconds(10),
				RunDeadline: TimeSpan.FromSeconds(10),
				SearchDeadline: TimeSpan.FromSeconds(10),
				MemoryLimitBytes: 1L * 1024 * 1024 * 1024,
				RunsBeforeRecycle: 50,
				BatchSize: 4),
			expectedEngine: "engine-under-test");

		var uninstalled = new BacktestService(
			_store, _store, _specs, _datasets, _store, _results, new WorkerBacktestRunner(host), new StockSharpMarketProfiler(), _store,
			_operations, new SystemClock());

		var broken = await ThrowsAsync<HarnessFailedException>(async () => await uninstalled.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, key, Actors.Agent, CancellationToken));

		AreEqual(HarnessFailures.Worker, broken.Kind,
			"a worker that could not be started was blamed on the candidate inside it.");

		AreEqual(before, await RemainingAsync(project),
			"a worker that was never installed was charged to the research allowance.");

		var recorded = await _store.ListAsync(project, candidate, CancellationToken);

		AreEqual(1, recorded.Count, "the attempt was hidden rather than recorded.");
		AreEqual(RunStatuses.Interrupted, recorded[0].Status,
			"a candidate that was never started was recorded as having failed.");

		// The operation key was not taken, so the same request measures something once the worker is
		// where it is supposed to be.
		var (run, wasAlreadyRun) = await _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, key, Actors.Agent, CancellationToken);

		IsFalse(wasAlreadyRun, "the retry was answered with the attempt that never ran.");
		AreEqual(RunStatuses.Completed, run.Status);
		AreEqual(before - 1, await RemainingAsync(project), "only the run that measured something was charged.");
	}

	/// <summary>
	/// A result that finished and could not be written down is this server's failure, not the strategy's,
	/// and is answered the same way: recorded as interrupted, not charged, and safe to ask again.
	/// </summary>
	[TestMethod]
	public async Task AResultThatCannotBeWrittenDownIsNotTheCandidatesFailure()
	{
		var (project, candidate) = await ReadyAsync();

		var before = await RemainingAsync(project);

		// The placeholder a run with nothing to show points at goes through; what the run produced does not.
		_results.Accept = 1;

		var broken = await ThrowsAsync<HarnessFailedException>(async () => await _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken));

		AreEqual(HarnessFailures.Storage, broken.Kind);

		IsFalse(broken.Message.Contains(@"C:\machine", StringComparison.Ordinal),
			$"a failure of ours must not carry this machine's paths to the caller: {broken.Message}");

		AreEqual(before, await RemainingAsync(project),
			"a result that could not be written down was charged to the research allowance.");

		var recorded = await _store.ListAsync(project, candidate, CancellationToken);

		AreEqual(RunStatuses.Interrupted, recorded[0].Status);
	}

	/// <summary>
	/// A store that will not take anything at all costs nothing and records nothing, because the attempt
	/// never reached the point of being one.
	/// </summary>
	/// <remarks>
	/// What makes an interrupted attempt recordable is that the empty result it points at is written
	/// before the allowance is claimed. A record that needed the store which has just failed would be
	/// missing exactly when it is wanted, and the claim would already have been taken for it.
	/// </remarks>
	[TestMethod]
	public async Task AStoreThatTakesNothingLeavesNothingBehind()
	{
		var (project, candidate) = await ReadyAsync();

		var before = await RemainingAsync(project);

		_results.Accept = 0;

		await ThrowsAsync<HarnessFailedException>(async () => await _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken));

		AreEqual(before, await RemainingAsync(project));
		AreEqual(0, _runner.Runs, "the candidate was run although its result could never have been kept.");
		AreEqual(0, (await _store.ListAsync(project, candidate, CancellationToken)).Count);
	}

	/// <summary>
	/// A candidate that reliably kills the process running it stops being free, because a failure that
	/// costs nothing can be asked for forever.
	/// </summary>
	/// <remarks>
	/// This is the hole the rest of it opens. The first few interruptions are given back, on the reading
	/// that the machine is flaky; after that the reading changes, because a machine that fails only on
	/// this one candidate is not what is being described. The attempt then becomes the candidate's own
	/// failure - recorded, charged, and returned as a result rather than raised as something to retry.
	/// </remarks>
	[TestMethod]
	public async Task ACandidateThatKeepsBreakingTheWorkerStopsBeingFree()
	{
		var (project, candidate) = await ReadyAsync();

		var before = await RemainingAsync(project);

		_runner.Isolation = IsolationFailures.Crashed;

		for (var attempt = 0; attempt < BacktestService.FreeInterruptions; attempt++)
		{
			await ThrowsAsync<HarnessFailedException>(async () => await _service.RunAsync(
				project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
				null, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken));
		}

		AreEqual(before, await RemainingAsync(project),
			$"the first {BacktestService.FreeInterruptions} interruptions are given back.");

		var (run, _) = await _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		AreEqual(RunStatuses.Failed, run.Status,
			"a candidate that reliably stops the process running it is an unlimited free run.");

		AreEqual(before - 1, await RemainingAsync(project), "the attempt past the cap is charged.");

		IsTrue(run.Error.Contains("cannot be run", StringComparison.Ordinal),
			$"the record must say why the candidate is being held answerable: {run.Error}");
	}

	/// <summary>
	/// A project whose backtests are all spent is refused another, the candidate is not run, and the
	/// refusal says it is the count that ran out.
	/// </summary>
	[TestMethod]
	public async Task ARunPastTheLastBacktestIsRefused()
	{
		var (project, candidate) = await ReadyAsync();

		IsTrue(await _store.TryClaimAsync(project, await RemainingAsync(project), 0, CancellationToken));

		var refusal = await ThrowsAsync<ResearchBudgetExhaustedException>(() => _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("backtests", StringComparison.Ordinal), refusal.Message);
		AreEqual(0, _runner.Runs, "a candidate was run on a budget that had nothing left.");
		AreEqual(0, await RemainingAsync(project), "the refusal changed what was left.");
	}

	/// <summary>
	/// A project whose machine time is spent is refused another run even with backtests left, and the
	/// refusal names the time rather than the count.
	/// </summary>
	[TestMethod]
	public async Task ARunPastTheLastMinuteIsRefused()
	{
		var (project, candidate) = await ReadyAsync();

		await _store.ChargeTimeAsync(project, TimeSpan.FromHours(1), CancellationToken);

		var before = await RemainingAsync(project);

		var refusal = await ThrowsAsync<ResearchBudgetExhaustedException>(() => _service.RunAsync(
			project, candidate, DataSlices.Development, RunWindow.Whole, "NVDA", RunScenario.Baseline,
			null, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("minutes of machine time", StringComparison.Ordinal), refusal.Message);
		AreEqual(0, _runner.Runs, "a candidate was run on a budget with no time left.");
		AreEqual(before, await RemainingAsync(project), "the refusal spent a backtest.");
	}

	private static IReadOnlyList<Candle> Bars()
	{
		var bars = new List<Candle>();
		var time = _open;

		for (var i = 0; i < 2_000; i++)
		{
			var wave = (decimal)Math.Sin(i * 2 * Math.PI / 60);
			var close = 100m + Math.Round(5m * wave, 2);
			var open = bars.Count == 0 ? close : bars[^1].Close;

			bars.Add(new(time, open, Math.Max(open, close) + 0.05m, Math.Min(open, close) - 0.05m, close, 10_000m));

			time = time.AddMinutes(5);

			if (time.TimeOfDay >= TimeSpan.FromHours(21))
				time = time.Date.AddDays(time.DayOfWeek == DayOfWeek.Friday ? 3 : 1).Add(_open.TimeOfDay);
		}

		return bars;
	}

	private static string Spec()
		=> $$"""
		{
		  "name": "Above its average {{Guid.NewGuid().ToString("n")[..8]}}",
		  "thesis": "A price above its own recent average keeps going for a few bars.",
		  "allowLong": true,
		  "allowShort": false,
		  "timeFrame": "00:05:00",
		  "warmupBars": 45,
		  "entries": [
		    { "id": "e1", "direction": "Long", "condition": {
		        "kind": "Compare",
		        "left": { "kind": "Field", "field": "Close" },
		        "operator": "GreaterThan",
		        "right": { "kind": "Indicator", "name": "sma", "length": { "kind": "Constant", "value": 20 }, "source": "Close" } } }
		  ],
		  "exits": [ { "id": "x1", "kind": "TimeExit", "direction": "Long", "length": { "kind": "Constant", "value": 5 } } ],
		  "parameters": [],
		  "risk": { "maxPositionPercent": 0.10, "maxDailyLossPercent": 0.02 }
		}
		""";

	private async Task<int> RemainingAsync(ProjectId project)
	{
		var existing = await _store.OpenAsync(project, CancellationToken);

		return ResearchBudget.Restore(existing.Budget).RemainingBacktests;
	}

	private async Task<(ProjectId Project, CandidateId Candidate)> ReadyAsync()
	{
		var created = await _projects.CreateProjectAsync(
			"budget", Guid.NewGuid().ToString("n"), Actors.User, CancellationToken);

		var project = created.Id;

		var bars = new Dictionary<string, IReadOnlyList<Candle>> { ["NVDA"] = Bars() };
		var imported = DatasetBuilder.Build(bars, TimeSpan.FromMinutes(5), "test", isSynthetic: true);

		await _datasets.SaveAsync(project, imported, CancellationToken);

		var opened = await _store.OpenAsync(project, CancellationToken);

		await _store.UpdateAsync(opened.WithDataset(imported.Manifest.Id, DateTime.UtcNow), CancellationToken);

		var spec = await _specs.AddAsync(project, Spec(), Actors.Agent, DateTime.UtcNow, CancellationToken);

		var built = await _candidates.BuildAsync(
			project, spec.Id, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		return (project, built.Id);
	}

	/// <summary>A builder that hands back something assembly-shaped, since nothing here runs it.</summary>
	private sealed class Builder : IStrategyBuilder
	{
		public BuiltStrategy Build(StrategySpec spec)
			=> new("Generated", $"// source of {spec.Name}", $"source-{spec.Name}", [1, 2, 3], $"assembly-{spec.Name}", "1.0.0");
	}

	/// <summary>A runner that can be told to give up the way a dropped client makes it give up.</summary>
	private sealed class Runner : IBacktestRunner
	{
		public bool Cancel { get; set; }

		public bool Fail { get; set; }

		public IsolationFailures? Isolation { get; set; }

		public int Runs { get; private set; }

		public Task<BacktestOutcome> RunAsync(BacktestRequest request, CancellationToken cancellationToken)
		{
			Runs++;

			if (Cancel)
				throw new OperationCanceledException();

			if (Fail)
				throw new InvalidOperationException("the strategy threw on its first bar");

			if (Isolation is { } kind)
			{
				throw new IsolationFailedException(kind, kind switch
				{
					IsolationFailures.Timeout => "The run was stopped after 5 minutes. Nothing was written.",
					IsolationFailures.Memory => "The run was stopped after using more than 2048 MB. Nothing was written.",
					IsolationFailures.Handshake => "The worker speaks protocol 2 and this server speaks 1.",
					_ => "The worker exited without answering.",
				});
			}

			var trades = new List<ExecutedTrade>();
			var equity = new List<EquityPoint>();
			var money = 100_000m;

			for (var i = 0; i < 20; i++)
			{
				var entry = request.Bars.From.AddHours(i);

				trades.Add(new($"t{i}", request.Symbol, TradeDirections.Long, entry, 100m, entry.AddMinutes(25),
					101m, 10m, 0.5m, 0.5m));

				money += 10m;
				equity.Add(new(entry.AddMinutes(25), money));
			}

			return Task.FromResult(new BacktestOutcome(trades, equity, request.Bars.Count, 0, 40));
		}
	}

	/// <summary>
	/// An artifact store that can be told to stop taking what it is given, which is what a full or
	/// unwritable disk looks like from the service.
	/// </summary>
	private sealed class Store(IArtifactStore inner) : IArtifactStore
	{
		/// <summary>How many writes to let through before refusing every one after them.</summary>
		public int Accept { get; set; } = int.MaxValue;

		public ValueTask<ArtifactDescriptor> PutAsync(
			ProjectId project,
			ReadOnlyMemory<byte> content,
			CancellationToken cancellationToken)
			=> Accept-- > 0
				? inner.PutAsync(project, content, cancellationToken)
				: throw new IOException(@"C:\machine\odysseus\artifacts is not writable");

		public ValueTask<byte[]> ReadAsync(ProjectId project, ArtifactId id, CancellationToken cancellationToken)
			=> inner.ReadAsync(project, id, cancellationToken);

		public ValueTask<bool> VerifyAsync(ProjectId project, ArtifactId id, CancellationToken cancellationToken)
			=> inner.VerifyAsync(project, id, cancellationToken);
	}
}
