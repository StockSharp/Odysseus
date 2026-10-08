namespace StockSharp.Odysseus.Application;

using System.Linq;

using StockSharp.Odysseus.Spec;

/// <summary>
/// What a deployment has done, next to what the backtest said it would.
/// </summary>
/// <param name="Deployment">The deployment as it is recorded.</param>
/// <param name="Runner">What was found when the process trading it was looked for.</param>
/// <param name="ExpectedTradesPerDay">Trades a day the closed-data run made.</param>
/// <param name="ObservedTradesPerDay">Trades a day it has made against the broker.</param>
/// <param name="ExpectedAverageTrade">What one trade came to in the closed-data run.</param>
/// <param name="ObservedAverageTrade">What one trade has come to against the broker.</param>
/// <param name="TradingDays">Trading days the deployment has been up, as a fraction.</param>
public sealed record DeploymentReport(
	Deployment Deployment,
	RunnerHandle Runner,
	decimal ExpectedTradesPerDay,
	decimal ObservedTradesPerDay,
	decimal ExpectedAverageTrade,
	decimal ObservedAverageTrade,
	decimal TradingDays)
{
	/// <summary>What the deployment amounts to now, which is not always what the row says.</summary>
	public DeploymentStatuses EffectiveStatus => DeploymentView.Effective(Deployment, Runner);
}

/// <summary>
/// A deployment as it stands, with what was found when its process was looked for.
/// </summary>
/// <param name="Deployment">The deployment as it is recorded.</param>
/// <param name="Runner">What was found when the process trading it was looked for.</param>
public sealed record DeploymentView(Deployment Deployment, RunnerHandle Runner)
{
	/// <summary>What the deployment amounts to now.</summary>
	public DeploymentStatuses EffectiveStatus => Effective(Deployment, Runner);

	/// <summary>
	/// What a row plus what was found of its process amount to.
	/// </summary>
	/// <param name="deployment">The deployment as it is recorded.</param>
	/// <param name="runner">What was found, or null when nothing was looked for.</param>
	/// <returns>What it amounts to.</returns>
	/// <remarks>
	/// The row is left as the process that wrote it left it - a reader does not decide that a deployment
	/// is over - so this is the reading, not a write. Two of the four findings change it and two do not,
	/// and which two is the whole reason there are four: a runner that is gone is nobody holding a
	/// position, and a runner that is alive and not answering is somebody holding one. Reporting the
	/// second as the first is the single most expensive mistake this type can make.
	/// </remarks>
	public static DeploymentStatuses Effective(Deployment deployment, RunnerHandle runner)
	{
		ArgumentNullException.ThrowIfNull(deployment);

		if (deployment.Status is not (DeploymentStatuses.Running or DeploymentStatuses.Starting))
			return deployment.Status;

		return runner?.Status switch
		{
			RunnerStatuses.Attached => DeploymentStatuses.Running,

			// Nothing is there, or nothing this build can identify is. Either way no process is adding to
			// a position, and what was left at the broker was left as it stood.
			RunnerStatuses.Gone or RunnerStatuses.Unknown => DeploymentStatuses.Interrupted,

			// Alive and not answering. Reported as what the row says, because it may well still be
			// trading, and a report of Interrupted would tell a reader that nobody is holding anything.
			_ => deployment.Status,
		};
	}
}

/// <summary>
/// Running a finished candidate against a live account.
/// </summary>
/// <remarks>
/// A backtest is a claim about a strategy; trading it is the first thing that can contradict the claim.
/// What the two disagree about is the whole point - a fill that never comes, a spread wider than the one
/// that was charged, a bar that arrives late - and none of it shows up in history.
///
/// A deployment does not live inside this process. It runs in a process of its own that outlives the
/// session that started it, outlives this server, and keeps trading until something stops it. That is the
/// feature and it is also the hazard: disconnecting no longer flattens anything, so whoever starts a
/// deployment is the one who has to stop it.
///
/// What this service holds is therefore a record and a way of finding a process, and never the process
/// itself. Nothing here caches a session: a map of live sessions dies with the process that owns it, and
/// everything it knew about the positions somebody is holding dies with it.
/// </remarks>
public sealed class DeploymentService
{
	private readonly IProjectStore _projects;
	private readonly ICandidateStore _candidates;
	private readonly ISpecStore _specs;
	private readonly IDatasetStore _datasets;
	private readonly IArtifactStore _artifacts;
	private readonly IRunStore _runs;
	private readonly IDeploymentStore _deployments;
	private readonly IRunnerHost _runners;
	private readonly IDeploymentConnector _connectors;
	private readonly IAuditLog _audit;
	private readonly IClock _clock;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="projects">Where projects are kept.</param>
	/// <param name="candidates">Where candidates are kept.</param>
	/// <param name="specs">Where specifications are kept.</param>
	/// <param name="datasets">Where datasets are kept.</param>
	/// <param name="artifacts">Where assemblies are kept.</param>
	/// <param name="runs">Where runs are kept.</param>
	/// <param name="deployments">Where deployments are kept.</param>
	/// <param name="runners">How the processes that trade are started, found and stopped.</param>
	/// <param name="connectors">Which connector a runner is told to load.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="clock">Source of the current moment.</param>
	public DeploymentService(
		IProjectStore projects,
		ICandidateStore candidates,
		ISpecStore specs,
		IDatasetStore datasets,
		IArtifactStore artifacts,
		IRunStore runs,
		IDeploymentStore deployments,
		IRunnerHost runners,
		IDeploymentConnector connectors,
		IAuditLog audit,
		IClock clock)
	{
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
		_specs = specs ?? throw new ArgumentNullException(nameof(specs));
		_datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
		_artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
		_runs = runs ?? throw new ArgumentNullException(nameof(runs));
		_deployments = deployments ?? throw new ArgumentNullException(nameof(deployments));
		_runners = runners ?? throw new ArgumentNullException(nameof(runners));
		_connectors = connectors ?? throw new ArgumentNullException(nameof(connectors));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>
	/// Starts a measured candidate in a process of its own.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to deploy.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The deployment.</returns>
	/// <remarks>
	/// The row is written before the process is started, and that order is what closes the gap between a
	/// process existing and anybody knowing about it. A crash before the runner greets leaves a row saying
	/// Starting with no process, which reads as interrupted and is true; a crash after it greets leaves a
	/// row saying Starting with a live process, which the runner's own greeting identifies. Neither
	/// produces an untracked runner or an untracked row.
	/// </remarks>
	public async ValueTask<Deployment> StartAsync(
		ProjectId project,
		CandidateId candidate,
		Actors actor,
		CancellationToken cancellationToken)
	{
		var existing = await _projects.OpenAsync(project, cancellationToken);
		var built = await _candidates.GetAsync(project, candidate, cancellationToken);

		// Whether the numbers are good enough is the researcher's call, not this server's. What the server
		// does require is that they exist: a candidate the closed data has never seen has nothing to be
		// contradicted by trading it, because nothing has been claimed about it yet.
		if (built.Status is not (CandidateStatuses.FinalChecked or CandidateStatuses.Completed))
		{
			throw new InvalidOperationException(
				$"This candidate is {built.Status}. Trading starts from the closed-data measurement: " +
				"run measure_on_closed_data first, read the numbers, and mark the candidate complete if " +
				"they are what you want to trade.");
		}

		var running = (await _deployments.ListAsync(project, cancellationToken))
			.FirstOrDefault(d => d.Status is DeploymentStatuses.Running or DeploymentStatuses.Starting);

		if (running is not null)
		{
			throw new InvalidOperationException(
				$"{running.Id} is already running {running.Candidate} on this project. Stop it before " +
				"starting another: two strategies on one account trade against each other's positions and " +
				"neither result means anything afterwards.");
		}

		if (existing.Dataset.IsEmpty)
			throw new InvalidOperationException("This project has no data, so there is no symbol to trade.");

		// Named rather than bound. The runner loads its own connector in its own process, so this server
		// does not need one loaded in order to deploy - it needs one named, and refuses when nothing has.
		var connector = _connectors.Choose();

		var manifest = await _datasets.GetManifestAsync(project, existing.Dataset, cancellationToken);
		var spec = SpecJson.Read((await _specs.GetAsync(project, built.Spec, cancellationToken)).Json);
		var assembly = await _artifacts.ReadAsync(project, built.Assembly, cancellationToken);

		var measured = OnClosedData(await _runs.ListAsync(project, candidate, cancellationToken));
		var symbol = measured?.Symbol ?? manifest.Symbols[0];

		var bars = await _datasets.LoadAsync(
			project, existing.Dataset, symbol, DataSlices.Development, cancellationToken);

		if (bars.Count == 0)
			throw new InvalidOperationException($"The project holds no bars for '{symbol}' to size a position against.");

		// Sized against the last price the project has seen, by the same rule the backtests used. The size
		// is fixed for the life of the deployment, so what it does can be compared with what was measured.
		var volume = BacktestService.PositionSize(spec, symbol, bars[^1].Close);

		// The numbers it was measured with, not the ones the specification declares as defaults: the closed
		// data answered one setting, and any other is a strategy nothing has run.
		var parameters = measured?.Parameters is { Count: > 0 } chosen ? chosen : Defaults(spec);

		var deployment = new Deployment(
			DeploymentId.New(),
			candidate,
			symbol,
			volume,
			TradingModes.Paper,
			0,
			DeploymentStatuses.Starting,
			_clock.UtcNow,
			null,
			0,
			0,
			0,
			0m,
			0m,
			null,
			null);

		await _deployments.AddAsync(project, deployment, cancellationToken);
		await _candidates.UpdateAsync(project, built.WithStatus(CandidateStatuses.PaperRunning, _clock.UtcNow), cancellationToken);

		RunnerHandle runner;

		try
		{
			runner = await _runners.LaunchAsync(
				new(
					deployment.Id.Value,
					project.Value,
					candidate.Value,
					built.ClassName,
					assembly,
					parameters,
					symbol,
					manifest.TimeFrame,
					volume,

					// The connector as it was named. Its settings cannot carry a credential or the demo
					// flag - the configurator refuses those names before anything is downloaded - so what
					// is written into the runner's home holds nothing secret.
					connector,

					// Always empty from here. Live trading is a property of a process this server cannot
					// create: it comes out of a file the operator named to the process that is going to
					// trade, and nothing reachable from a tool puts a path here.
					string.Empty),
				cancellationToken);
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			// It never traded. The row says so with the reason on it, rather than being deleted: a
			// deployment that failed to start is something a person may want to read afterwards, and its
			// home is left where it is for the same reason.
			await _deployments.UpdateAsync(
				project,
				deployment with
				{
					Status = DeploymentStatuses.Failed,
					StoppedAt = _clock.UtcNow,
					Note = error.Message,
				},
				cancellationToken);

			await Release(project, candidate, cancellationToken);

			throw;
		}

		var started = deployment with
		{
			Status = DeploymentStatuses.Running,
			Mode = runner.Mode,
			ProcessId = runner.ProcessId,
		};

		await _deployments.UpdateAsync(project, started, cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.PaperStarted,
			actor,
			$"Started {built.ClassName} on {symbol} against {connector.PackageId} at {volume} per position" +
			$"{(runner.Mode == TradingModes.Live ? " - LIVE." : ".")}",
			built.SourceHash,
			cancellationToken);

		return started;
	}

	/// <summary>
	/// Reports what a deployment has done, next to what was measured of it.
	/// </summary>
	/// <param name="project">Project the deployment belongs to.</param>
	/// <param name="deployment">Deployment to report on.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The report.</returns>
	public async ValueTask<DeploymentReport> GetAsync(
		ProjectId project,
		DeploymentId deployment,
		CancellationToken cancellationToken)
	{
		var (recorded, runner) = await Observe(
			project, await _deployments.GetAsync(project, deployment, cancellationToken), cancellationToken);

		var measured = OnClosedData(await _runs.ListAsync(project, recorded.Candidate, cancellationToken));

		// Over the days it received candles for: nights, weekends and outages are not trading days.
		var days = (decimal)recorded.SessionDays;

		var expectedTrades = 0m;
		var expectedAverage = 0m;

		if (measured?.Metrics is { } metrics && metrics.Trades.Count > 0)
		{
			var existing = await _projects.OpenAsync(project, cancellationToken);

			// How much market the run covered, in the unit the deployment is measured in: the bars it saw,
			// over how many bars a trading day of this data holds.
			var bars = await _datasets.LoadAsync(project, existing.Dataset, recorded.Symbol, measured.Slice, cancellationToken);
			var sliceDays = bars.Select(b => b.OpenTime.Date).Distinct().Count();
			var covered = sliceDays == 0 ? 0m : measured.BarsProcessed / ((decimal)bars.Count / sliceDays);

			expectedTrades = covered > 0 ? Round(metrics.Trades.Count / covered) : 0m;
			expectedAverage = Round(metrics.Net.Profit / metrics.Trades.Count);
		}

		return new(
			recorded,
			runner,
			expectedTrades,
			days > 0 ? Round(recorded.Trades / days) : 0m,
			expectedAverage,
			recorded.Trades > 0 ? Round(recorded.RealizedProfit / recorded.Trades) : 0m,
			Round(days));
	}

	/// <summary>
	/// Lists the deployments of a project, newest first.
	/// </summary>
	/// <param name="project">Project to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The deployments, each with what was found of the process trading it.</returns>
	public async ValueTask<IReadOnlyList<DeploymentView>> ListAsync(ProjectId project, CancellationToken cancellationToken)
	{
		var listed = await _deployments.ListAsync(project, cancellationToken);
		var seen = new List<DeploymentView>(listed.Count);

		foreach (var deployment in listed)
		{
			var (observed, runner) = await Observe(project, deployment, cancellationToken);

			seen.Add(new(observed, runner));
		}

		return seen;
	}

	/// <summary>
	/// Every runner on this machine, whether or not a row here knows about it.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The runners.</returns>
	/// <remarks>
	/// The registry is per machine and the rows are per project, so the two can disagree - a runner whose
	/// project was exported, or one started against a different projects root and reachable from this one.
	/// Reported rather than hidden: an unaccounted-for runner is the thing most worth knowing about.
	/// </remarks>
	public ValueTask<IReadOnlyList<RunnerHandle>> RunnersAsync(CancellationToken cancellationToken)
		=> _runners.ListAsync(cancellationToken);

	/// <summary>
	/// Reads the account a deployment's own runner is trading on.
	/// </summary>
	/// <param name="deployment">Deployment whose account to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The account, its holdings and its live orders.</returns>
	/// <remarks>
	/// Through the runner's own connection rather than through this server's, because they need not be
	/// the same account and only one of the two is the one holding the position.
	/// </remarks>
	public ValueTask<PaperAccountState> AccountAsync(DeploymentId deployment, CancellationToken cancellationToken)
		=> _runners.AccountAsync(deployment.Value, cancellationToken);

	/// <summary>
	/// Stops a deployment.
	/// </summary>
	/// <param name="project">Project the deployment belongs to.</param>
	/// <param name="deployment">Deployment to stop.</param>
	/// <param name="closePosition">Whether to close what it is holding.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The deployment as it ended.</returns>
	/// <exception cref="RunnerUnresponsiveException">
	/// Its process is alive and not answering, so nothing was stopped and nothing was recorded.
	/// </exception>
	/// <exception cref="RunnerStopFailedException">
	/// The runner answered that it did not stop, so the deployment stays running and the candidate held.
	/// </exception>
	public async ValueTask<Deployment> StopAsync(
		ProjectId project,
		DeploymentId deployment,
		bool closePosition,
		Actors actor,
		CancellationToken cancellationToken)
	{
		var recorded = await _deployments.GetAsync(project, deployment, cancellationToken);

		// Raises rather than records when the process is alive and silent. Recording that as stopped would
		// say something untrue about an open position: nothing was stopped, nothing was closed, and the
		// process may still be trading.
		var runner = await _runners.StopAsync(deployment.Value, closePosition, cancellationToken);

		if (runner.Status == RunnerStatuses.Attached && runner.State is { } unfinished && unfinished.Phase != RunnerPhases.Stopped)
		{
			// Written down as observed and left running: the strategy may still hold a position, and a row
			// saying Stopped would release the candidate while it does.
			await _deployments.UpdateAsync(
				project, Observed(recorded, runner) with { Note = unfinished.Error }, cancellationToken);

			throw new RunnerStopFailedException(
				$"{deployment} did not stop: {unfinished.Error ?? $"the runner answered '{unfinished.Phase}'."} " +
				"It is still recorded as running, and the candidate is still held.");
		}

		if (runner.Status == RunnerStatuses.Attached && runner.State is { } ended)
		{
			recorded = Observed(recorded, runner) with
			{
				Status = DeploymentStatuses.Stopped,
				StoppedAt = _clock.UtcNow,
				Note = ended.Error ?? (closePosition
					? "Stopped, closing what it held."
					: $"Stopped, leaving a position of {ended.Position} open at the broker."),
			};
		}
		else if (recorded.Status is DeploymentStatuses.Running or DeploymentStatuses.Starting)
		{
			// The process that owned it is gone, or is one this build will not talk to. Nothing was
			// stopped in an orderly fashion, and the runner's own journal is what the note carries -
			// which is more than the last observation happened to catch.
			recorded = recorded with
			{
				Status = DeploymentStatuses.Interrupted,
				StoppedAt = _clock.UtcNow,
				Note = runner.Detail,
			};
		}

		await _deployments.UpdateAsync(project, recorded, cancellationToken);

		// Reached whatever the row said on the way in. A deployment that had already ended - by itself, or
		// with the process that owned it - still leaves a candidate marked as trading, and this is the call
		// that says it is not. Returning early because the row was no longer Running left that candidate
		// with no way out: it could not be stopped, and it could not be deployed again either.
		var built = await Release(project, recorded.Candidate, cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.PaperStopped,
			actor,
			$"Stopped {deployment} after {recorded.Trades} trade(s) and {recorded.RealizedProfit:F2} realized" +
			$"{(closePosition ? ", closing what it held" : string.Empty)}" +
			$"{(recorded.Mode == TradingModes.Live ? " - LIVE" : string.Empty)}.",
			built.SourceHash,
			cancellationToken);

		return recorded;
	}

	private static RunResult OnClosedData(IReadOnlyList<RunResult> runs)
		=> runs
			.Where(r => r.Slice == DataSlices.Final && r.Status == RunStatuses.Completed)
			.OrderByDescending(r => r.FinishedAt)
			.FirstOrDefault();

	private static IReadOnlyDictionary<string, decimal> Defaults(StrategySpec spec)
		=> spec.Parameters.ToDictionary(p => p.Name, p => p.Default, StringComparer.Ordinal);

	private static decimal Round(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);

	/// <summary>
	/// Looks for the process trading a deployment, and writes down only what it said.
	/// </summary>
	/// <param name="project">Project the deployment belongs to.</param>
	/// <param name="deployment">The deployment as it is recorded.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The deployment and what was found.</returns>
	/// <remarks>
	/// Reading must not end a deployment. This used to write Interrupted for one whose session had gone,
	/// which made looking at a deployment end it: stopping then found a row that was no longer Running and
	/// returned without releasing the candidate or recording anything. Whether a deployment is over is
	/// decided by stopping it, not by somebody reading the list - so the only writes here are counters a
	/// runner has just reported, and a failure a runner has just reported about itself.
	/// </remarks>
	private async ValueTask<(Deployment Deployment, RunnerHandle Runner)> Observe(
		ProjectId project,
		Deployment deployment,
		CancellationToken cancellationToken)
	{
		if (deployment.Status is not (DeploymentStatuses.Running or DeploymentStatuses.Starting))
			return (deployment, null);

		var runner = await _runners.AttachAsync(deployment.Id.Value, cancellationToken);

		if (runner.Status != RunnerStatuses.Attached || runner.State is null)
			return (deployment, runner);

		var observed = Observed(deployment, runner);

		if (!runner.State.IsRunning)
		{
			// It stopped itself, and that is the runner's own report rather than an inference from its
			// absence, so it is recorded.
			observed = observed with
			{
				Status = DeploymentStatuses.Failed,
				StoppedAt = _clock.UtcNow,
				Note = runner.State.Error ?? "The strategy stopped by itself.",
			};
		}
		else if (deployment.Status == DeploymentStatuses.Starting)
		{
			// It greeted after the row was written, which is the ordinary case and also the one a crash
			// between the two leaves behind. Either way the runner is the authority on it.
			observed = observed with { Status = DeploymentStatuses.Running };
		}

		await _deployments.UpdateAsync(project, observed, cancellationToken);

		return (observed, runner);
	}

	private async ValueTask<Candidate> Release(ProjectId project, CandidateId candidate, CancellationToken cancellationToken)
	{
		var built = await _candidates.GetAsync(project, candidate, cancellationToken);

		if (built.Status != CandidateStatuses.PaperRunning)
			return built;

		var released = built.WithStatus(CandidateStatuses.Stopped, _clock.UtcNow);

		await _candidates.UpdateAsync(project, released, cancellationToken);

		return released;
	}

	private Deployment Observed(Deployment deployment, RunnerHandle runner)
		=> deployment with
		{
			Mode = runner.Mode,
			ProcessId = runner.ProcessId,
			OrdersPlaced = runner.State.OrdersPlaced,
			Trades = runner.State.Trades,
			SessionDays = runner.State.SessionDays,
			RealizedProfit = Round(runner.State.RealizedProfit),
			Position = runner.State.Position,

			// Stamped with the numbers it belongs to. Without it, an outage is indistinguishable from a
			// quiet market: both leave the same figures behind, and only one of them means the strategy
			// did nothing.
			LastObservedAt = _clock.UtcNow,
		};
}
