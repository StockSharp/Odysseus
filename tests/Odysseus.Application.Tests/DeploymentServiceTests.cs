namespace StockSharp.Odysseus.Application.Tests;

using System.Threading;

using StockSharp.Odysseus.Persistence;
using StockSharp.Odysseus.Platform;
using StockSharp.Odysseus.Spec;

/// <summary>
/// Putting a candidate on an account, in a process that outlives the session that started it.
/// </summary>
/// <remarks>
/// This is where research stops being free. Everything before it happens against stored bars; here a
/// strategy places orders on an account, and the rules about which candidate may do that, with which
/// numbers, and how many at once are the whole of what stands between research and a mess.
///
/// The runner is a stand-in, so these say nothing about whether a venue fills anything. They say what is
/// allowed to reach it, and what is written down about what did - which is the half that changed when a
/// deployment stopped being something this process holds and became something it can find.
/// </remarks>
[TestClass]
public class DeploymentServiceTests : OdysseusTestBase
{
	private string _root;
	private FileProjectStore _store;
	private FileOperationLog _operations;
	private FileDatasetStore _datasets;
	private FileSpecStore _specs;
	private FileArtifactStore _artifacts;
	private Runners _runners;
	private ProjectService _projects;
	private DatasetService _dataset;
	private CandidateService _candidates;
	private DeploymentService _service;

	/// <summary>Builds the services over temporary storage.</summary>
	[TestInitialize]
	public void CreateService()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new FileProjectStore(_root);
		_operations = new FileOperationLog(_root);
		_artifacts = new FileArtifactStore(_root);
		_datasets = new FileDatasetStore(_root, new LocalBarStorage(Path.Combine(_root, "market-data")));
		_specs = new FileSpecStore(_root);
		_runners = new Runners();

		var clock = new SystemClock();
		var budget = new ResearchBudget(400, 40, TimeSpan.FromMinutes(45)).ToState();

		_projects = new ProjectService(_store, _store, _operations, clock, ServerModes.Local, budget);
		_dataset = new DatasetService(_store, _datasets, _store, _store, _operations, clock);

		_candidates = new CandidateService(
			_store, _specs, _store, _artifacts, new Builder(), _store, _operations, clock);

		_service = new DeploymentService(
			_store, _store, _specs, _datasets, _artifacts, _store, _store, _runners, new Named(), _store, clock);
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

	/// <summary>
	/// A candidate the closed data has never seen cannot be put on an account. Paper is where a backtest
	/// gets contradicted, and there is nothing to contradict until something has been claimed.
	/// </summary>
	[TestMethod]
	public async Task ACandidateWithoutClosedDataIsRefused()
	{
		var (project, candidate) = await ReadyAsync();

		var refusal = await ThrowsAsync<InvalidOperationException>(
			() => _service.StartAsync(project, candidate, Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("measure_on_closed_data", StringComparison.OrdinalIgnoreCase),
			$"the refusal does not say what is missing: {refusal.Message}");
	}

	/// <summary>A finished candidate runs, and the account trades the symbol it was measured on.</summary>
	[TestMethod]
	public async Task AFinishedCandidateReachesTheBroker()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		var deployment = await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		AreEqual(DeploymentStatuses.Running, deployment.Status);
		IsTrue(deployment.Volume > 0, "a deployment was started with nothing to trade.");

		IsNotNull(_runners.Started, "no runner was ever started.");
		AreEqual(deployment.Symbol, _runners.Started.Symbol, "the runner was pointed at a different symbol.");
		AreEqual("StockSharp.Example", _runners.Started.Connector.PackageId,
			"the runner was not told which connector to load.");

		IsTrue(string.IsNullOrEmpty(_runners.Started.MandatePath),
			"this server handed a runner a path to a live mandate, which it must never do.");

		AreEqual(24188, deployment.ProcessId, "the deployment does not record the process trading it.");
		AreEqual(TradingModes.Paper, deployment.Mode);

		var frozen = await _dataset.GetManifestAsync(project, CancellationToken);

		IsTrue(frozen.Symbols.Contains(deployment.Symbol, StringComparer.Ordinal),
			$"it is trading '{deployment.Symbol}', which the project holds no data for.");

		var reopened = await _store.GetAsync(project, candidate, CancellationToken);

		AreEqual(CandidateStatuses.PaperRunning, reopened.Status, "the candidate does not say it is running.");
	}

	/// <summary>
	/// One at a time. Two strategies on one account hold each other's positions and neither result can be
	/// read afterwards.
	/// </summary>
	[TestMethod]
	public async Task ASecondDeploymentIsRefusedWhileOneRuns()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		var second = await ReadyAsync(CandidateStatuses.Completed, project);

		var refusal = await ThrowsAsync<InvalidOperationException>(
			() => _service.StartAsync(project, second.Candidate, Actors.Agent, CancellationToken).AsTask());

		IsTrue(refusal.Message.Contains("already running", StringComparison.OrdinalIgnoreCase),
			$"the refusal does not say why: {refusal.Message}");
	}

	/// <summary>What the broker reports is what the deployment reports; nothing here is the strategy's own view.</summary>
	[TestMethod]
	public async Task WhatTheBrokerReportsIsWhatIsReported()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		var deployment = await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		_runners.State = _runners.State with
		{
			OrdersPlaced = 7,
			Trades = 3,
			RealizedProfit = 12.5m,
			Position = 2m,
		};

		var report = await _service.GetAsync(project, deployment.Id, CancellationToken);

		AreEqual(7, report.Deployment.OrdersPlaced);
		AreEqual(3, report.Deployment.Trades);
		AreEqual(12.5m, report.Deployment.RealizedProfit);
		AreEqual(2m, report.Deployment.Position);
	}

	/// <summary>Stopping says what to do with the position, and the broker is told exactly that.</summary>
	[TestMethod]
	public async Task StoppingCarriesTheDecisionAboutThePosition()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		var deployment = await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		_runners.State = _runners.State with { Position = 5m };

		var stopped = await _service.StopAsync(project, deployment.Id, closePosition: true, Actors.Agent, CancellationToken);

		AreEqual(DeploymentStatuses.Stopped, stopped.Status);
		IsTrue(_runners.WasStopped, "the runner was never told to stop.");
		IsTrue(_runners.WasAskedToClose, "the position was left open despite being told to close it.");

		var reopened = await _store.GetAsync(project, candidate, CancellationToken);

		AreEqual(CandidateStatuses.Stopped, reopened.Status);
	}

	/// <summary>
	/// A runner that answers a stop by saying the stop failed has not stopped. Recording it as stopped
	/// would release the candidate while the strategy may still hold a position at the broker.
	/// </summary>
	[TestMethod]
	public async Task AStopThatFailedIsNotRecordedAsStopped()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		var deployment = await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		_runners.State = _runners.State with { Position = 5m };
		_runners.StopFails = true;

		await ThrowsAsync<RunnerStopFailedException>(
			() => _service.StopAsync(project, deployment.Id, closePosition: true, Actors.Agent, CancellationToken).AsTask());

		var stored = await _store.GetAsync(project, deployment.Id, CancellationToken);

		AreNotEqual(DeploymentStatuses.Stopped, stored.Status,
			"a deployment whose stop failed was recorded as stopped.");

		var built = await _store.GetAsync(project, candidate, CancellationToken);

		AreEqual(CandidateStatuses.PaperRunning, built.Status,
			"the candidate was released although its stop failed and a position may still be open.");
	}

	/// <summary>
	/// A strategy that stopped by itself is reported as failed rather than as running, and carries the
	/// reason. A caller told it is running will not look, and it is not.
	/// </summary>
	[TestMethod]
	public async Task AStrategyThatStoppedItselfSaysSo()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		var deployment = await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		_runners.State = _runners.State with { IsRunning = false, Error = "the venue rejected every order" };

		var report = await _service.GetAsync(project, deployment.Id, CancellationToken);

		AreEqual(DeploymentStatuses.Failed, report.Deployment.Status);
		AreEqual("the venue rejected every order", report.Deployment.Note);
	}

	/// <summary>
	/// A deployment recorded as running by a server that no longer exists is reported as interrupted, so
	/// nobody is told a strategy is trading when nothing is.
	/// </summary>
	[TestMethod]
	public async Task ADeploymentLeftByADeadServerIsInterrupted()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		var deployment = await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		var report = await Restarted().GetAsync(project, deployment.Id, CancellationToken);

		AreEqual(DeploymentStatuses.Interrupted, report.EffectiveStatus);
		AreEqual(RunnerStatuses.Gone, report.Runner.Status, "a deployment whose process is gone was reported as attached.");
	}

	/// <summary>
	/// Reading does not stop anything. What the row says is what the server that wrote it said; a reader
	/// that rewrote it would be deciding, on its own, that the deployment is over.
	/// </summary>
	/// <remarks>
	/// It also destroyed the only path out. The read wrote Interrupted, stop_deployment then returned
	/// early because the row was no longer Running, and the candidate was left at PaperRunning for good:
	/// it could not be stopped, could not be redeployed, and the tool the refusal pointed at refused it
	/// too. Whether a deployment is over is decided by stopping it.
	/// </remarks>
	[TestMethod]
	public async Task ReadingADeploymentDoesNotEndIt()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		var deployment = await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		var restarted = Restarted();

		await restarted.GetAsync(project, deployment.Id, CancellationToken);
		await restarted.ListAsync(project, CancellationToken);

		var stored = await _store.GetAsync(project, deployment.Id, CancellationToken);

		AreEqual(DeploymentStatuses.Running, stored.Status,
			"reading the deployment wrote to it.");

		var built = await _store.GetAsync(project, candidate, CancellationToken);

		AreEqual(CandidateStatuses.PaperRunning, built.Status);
	}

	/// <summary>
	/// Stopping what a dead server left behind still ends it properly: the row becomes terminal, the
	/// candidate is released, and the permanent record says it happened.
	/// </summary>
	[TestMethod]
	public async Task StoppingAfterTheServerWentAwayReleasesTheCandidate()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		var deployment = await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		var restarted = Restarted();

		// The order a person actually takes: look first, then stop.
		await restarted.ListAsync(project, CancellationToken);

		var stopped = await restarted.StopAsync(project, deployment.Id, closePosition: false, Actors.Agent, CancellationToken);

		AreEqual(DeploymentStatuses.Interrupted, stopped.Status);

		var built = await _store.GetAsync(project, candidate, CancellationToken);

		AreEqual(CandidateStatuses.Stopped, built.Status,
			"the candidate was left running on paper although its deployment is over.");

		var audited = await _store.ReadAsync(project, CancellationToken);

		IsTrue(audited.Any(e => e.Type == AuditEventTypes.PaperStopped),
			"nothing in the permanent record says the deployment ended.");
	}

	/// <summary>
	/// What the deployment did is measured over the days it received candles for: nights, weekends and
	/// the hours nobody was watching are not trading days, and a day of daily candles is one day.
	/// </summary>
	[TestMethod]
	public async Task TheRateIsMeasuredOverTheDaysItReceivedCandlesFor()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		var deployment = await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		_runners.State = _runners.State with { Trades = 5, SessionDays = 2 };

		var report = await _service.GetAsync(project, deployment.Id, CancellationToken);

		AreEqual(2m, report.TradingDays, "the rate was not measured over the days candles arrived for.");
		AreEqual(2.5m, report.ObservedTradesPerDay, "five trades over two trading days is two and a half a day.");
	}

	/// <summary>
	/// Stopping a runner that is alive and not answering writes nothing down and refuses. It is the one
	/// refusal that exists to stop a lie being recorded: nothing was stopped, nothing was closed, and the
	/// process may still be trading, so a row saying Stopped would say something untrue about an open
	/// position.
	/// </summary>
	[TestMethod]
	public async Task StoppingAnUnresponsiveRunnerRecordsNothingAndRefuses()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		var deployment = await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		var service = Restarted(RunnerStatuses.Unresponsive);

		await ThrowsAsync<RunnerUnresponsiveException>(
			() => service.StopAsync(project, deployment.Id, closePosition: true, Actors.Agent, CancellationToken).AsTask());

		var stored = await _store.GetAsync(project, deployment.Id, CancellationToken);

		AreEqual(DeploymentStatuses.Running, stored.Status,
			"a deployment nothing could be said about was recorded as having ended.");

		var built = await _store.GetAsync(project, candidate, CancellationToken);

		AreEqual(CandidateStatuses.PaperRunning, built.Status,
			"the candidate was released although the strategy may still be trading.");
	}

	/// <summary>
	/// A runner that is alive and not answering is not reported as interrupted. Only one of the two
	/// findings means nobody is holding a position, and this is the other one.
	/// </summary>
	[TestMethod]
	public async Task AnUnresponsiveRunnerIsNotReportedAsInterrupted()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		var deployment = await _service.StartAsync(project, candidate, Actors.Agent, CancellationToken);

		var report = await Restarted(RunnerStatuses.Unresponsive).GetAsync(project, deployment.Id, CancellationToken);

		AreEqual(DeploymentStatuses.Running, report.EffectiveStatus,
			"a process that is alive and silent was reported as one that has ended.");

		AreEqual(RunnerStatuses.Unresponsive, report.Runner.Status);
	}

	/// <summary>
	/// A runner that would not start leaves the deployment recorded as failed with the reason, and gives
	/// the candidate back - so a project is not left with a candidate that can be neither stopped nor
	/// deployed again.
	/// </summary>
	[TestMethod]
	public async Task ARunnerThatWouldNotStartLeavesTheCandidateFree()
	{
		var (project, candidate) = await ReadyAsync(CandidateStatuses.Completed);

		_runners.Refuse = true;

		await ThrowsAsync<RunnerStartFailedException>(
			() => _service.StartAsync(project, candidate, Actors.Agent, CancellationToken).AsTask());

		var deployments = await Restarted().ListAsync(project, CancellationToken);

		AreEqual(1, deployments.Count, "a deployment that failed to start left no record of the attempt.");
		AreEqual(DeploymentStatuses.Failed, deployments[0].Deployment.Status);

		var built = await _store.GetAsync(project, candidate, CancellationToken);

		AreEqual(CandidateStatuses.Stopped, built.Status,
			"the candidate was left running on paper although nothing ever started.");
	}

	// A distinct name each time, so two calls produce two candidates rather than one twice.
	private static string Spec(string name)
		=> $$"""
		{
		  "name": "Above its average {{{name}}}",
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

	/// <summary>
	/// A new service over the same storage, with the runner gone. That is what a session finds after the
	/// process trading a deployment has ended - and it is a different finding from a runner that is alive
	/// and not answering, which is why the stand-in is told which of the two to be.
	/// </summary>
	/// <param name="found">What is to be found when a runner is looked for.</param>
	/// <returns>The service.</returns>
	private DeploymentService Restarted(RunnerStatuses found = RunnerStatuses.Gone)
	{
		_runners.Found = found;

		return new(_store, _store, _specs, _datasets, _artifacts, _store, _store, _runners, new Named(), _store, new SystemClock());
	}

	private async Task<(ProjectId Project, CandidateId Candidate)> ReadyAsync(
		CandidateStatuses status = CandidateStatuses.Compiled,
		ProjectId project = default)
	{
		if (project.IsEmpty)
		{
			var created = await _projects.CreateProjectAsync("paper", Guid.NewGuid().ToString("n"), Actors.User, CancellationToken);

			project = created.Id;

			await _dataset.ImportDemoAsync(project, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);
		}

		var spec = await _specs.AddAsync(project, Spec(Guid.NewGuid().ToString("n")[..8]), Actors.Agent, DateTime.UtcNow, CancellationToken);

		var built = await _candidates.BuildAsync(project, spec.Id, Guid.NewGuid().ToString("n"), Actors.Agent, CancellationToken);

		// The lifecycle refuses a jump, so a candidate that is to arrive at a later stage walks there.
		if (status != CandidateStatuses.Compiled)
		{
			CandidateStatuses[] road =
			[
				CandidateStatuses.Backtested,
				CandidateStatuses.Validated,
				CandidateStatuses.StressTested,
				CandidateStatuses.FinalChecked,
				status,
			];

			foreach (var stage in road)
			{
				built = built.WithStatus(stage, DateTime.UtcNow);

				await _store.UpdateAsync(project, built, CancellationToken);
			}
		}

		return (project, built.Id);
	}

	/// <summary>A builder that hands back something assembly-shaped, since nothing here runs it.</summary>
	private sealed class Builder : IStrategyBuilder
	{
		public BuiltStrategy Build(StrategySpec spec)
			=> new(
				"Generated",
				$"// source of {spec.Name}",

				// Distinct per specification, because a candidate is recognised by the source it came from
				// and two specifications hashing the same are one candidate.
				$"source-{spec.Name}",
				[1, 2, 3],
				$"assembly-{spec.Name}",
				"1.0.0");
	}

	/// <summary>
	/// Runners that behave however a test needs them to, without a process anywhere.
	/// </summary>
	/// <remarks>
	/// Two states matter more than the rest and are easy to conflate: a runner that is gone, and one that
	/// is alive and not answering. Only the first means nobody is holding a position, so a stand-in that
	/// could not produce both would leave the more expensive of the two untested.
	/// </remarks>
	private sealed class Runners : IRunnerHost
	{
		private readonly Dictionary<string, RunnerHandle> _handles = new(StringComparer.Ordinal);

		public RunnerLaunch Started { get; private set; }

		public bool WasAskedToClose { get; private set; }

		public bool WasStopped { get; private set; }

		public bool Refuse { get; set; }

		/// <summary>Whether the runner answers a stop by saying the stop itself failed.</summary>
		public bool StopFails { get; set; }

		/// <summary>What a runner reports about itself. Changing it is how a test makes the world move.</summary>
		public RunnerState State { get; set; } = Trading;

		/// <summary>What is found when a runner is looked for.</summary>
		public RunnerStatuses Found { get; set; } = RunnerStatuses.Attached;

		private static RunnerState Trading
			=> new(
				DateTime.UtcNow, RunnerPhases.Trading, TradingModes.Paper, true,
				0, 0, 0, 0m, 0m, 0, "stand-in-account", null, null);

		public ValueTask<RunnerHandle> LaunchAsync(RunnerLaunch launch, CancellationToken cancellationToken)
		{
			if (Refuse)
				throw new RunnerStartFailedException("the runner would not start");

			Started = launch;

			var handle = new RunnerHandle(
				launch.DeploymentId, launch.ProjectId, launch.CandidateId, RunnerStatuses.Attached,
				TradingModes.Paper, 24188, "stand-in", "home", State, "Connected and answering.");

			_handles[launch.DeploymentId] = handle;

			return ValueTask.FromResult(handle);
		}

		public ValueTask<RunnerHandle> AttachAsync(string deploymentId, CancellationToken cancellationToken)
			=> ValueTask.FromResult(Handle(deploymentId));

		public ValueTask<RunnerHandle> StopAsync(string deploymentId, bool closePosition, CancellationToken cancellationToken)
		{
			if (Found == RunnerStatuses.Unresponsive)
			{
				throw new RunnerUnresponsiveException(
					"Process 24188 is alive and not answering. Nothing was recorded, because recording this " +
					"as stopped would say something untrue about an open position.");
			}

			WasStopped = true;
			WasAskedToClose = closePosition;

			if (Found == RunnerStatuses.Attached)
			{
				State = StopFails
					? State with { Phase = RunnerPhases.Failed, Error = "The broker refused to cancel the working orders." }
					: State with { IsRunning = false, Phase = RunnerPhases.Stopped };
			}

			return ValueTask.FromResult(Handle(deploymentId));
		}

		public ValueTask<PaperAccountState> AccountAsync(string deploymentId, CancellationToken cancellationToken)
			=> throw new NotSupportedException();

		public ValueTask<IReadOnlyList<RunnerHandle>> ListAsync(CancellationToken cancellationToken)
			=> ValueTask.FromResult<IReadOnlyList<RunnerHandle>>([.. _handles.Keys.Select(Handle)]);

		private RunnerHandle Handle(string deploymentId)
			=> new(
				deploymentId,
				"prj",
				"cnd",
				Found,
				TradingModes.Paper,
				Found == RunnerStatuses.Unknown ? 0 : 24188,
				"stand-in",
				"home",
				Found == RunnerStatuses.Attached ? State : null,
				Found switch
				{
					RunnerStatuses.Gone => "There is no process 24188. It left what it held as it stood.",
					RunnerStatuses.Unresponsive => "Process 24188 is alive and did not answer.",
					RunnerStatuses.Unknown => "No runner was ever recorded for this deployment.",
					_ => "Connected and answering.",
				});
	}

	/// <summary>A connector that is named and never loaded, which is all a deployment needs of one.</summary>
	private sealed class Named : IDeploymentConnector
	{
		public ConnectorChoice Choose() => new("StockSharp.Example", "1.2.3", string.Empty, null);
	}
}
