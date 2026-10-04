namespace Odysseus.Application;

using System.Diagnostics;
using System.Linq;
using System.Text.Json;

using Odysseus.Evaluation;
using Odysseus.Spec;

/// <summary>
/// What one walk-forward window chose, and what that came to on the stretch it had not seen.
/// </summary>
/// <param name="InSampleFrom">Start of the stretch the numbers were fitted on, in UTC.</param>
/// <param name="InSampleTo">End of the stretch the numbers were fitted on, in UTC.</param>
/// <param name="OutOfSampleFrom">Start of the stretch they were tested on, in UTC.</param>
/// <param name="OutOfSampleTo">End of the stretch they were tested on, in UTC.</param>
/// <param name="Parameters">The numbers the window chose.</param>
/// <param name="InSampleFitness">What they scored where they were chosen.</param>
/// <param name="OutOfSample">What they came to on the stretch after it.</param>
public sealed record WalkForwardWindowReport(
	DateTime InSampleFrom,
	DateTime InSampleTo,
	DateTime OutOfSampleFrom,
	DateTime OutOfSampleTo,
	IReadOnlyDictionary<string, decimal> Parameters,
	decimal InSampleFitness,
	RunMetrics OutOfSample);

/// <summary>
/// What a walk-forward came to.
/// </summary>
/// <param name="Windows">Each window, oldest first.</param>
/// <param name="WasAlreadyRun">Whether this answers a request already made under the same key.</param>
public sealed record WalkForwardOutcome(IReadOnlyList<WalkForwardWindowReport> Windows, bool WasAlreadyRun);

/// <summary>
/// What a search over a candidate's numbers found.
/// </summary>
/// <param name="Chosen">The setting the search ranked first.</param>
/// <param name="Trials">Every setting it evaluated, best first.</param>
/// <param name="Run">The run of the chosen setting, measured the ordinary way.</param>
/// <param name="Seed">What the search's randomness started from, so it can be repeated.</param>
/// <param name="WasAlreadySearched">
/// Whether this answer is the one an earlier call with the same key already produced. A replay carries
/// no trials: only the setting that was chosen and the run that measured it are kept.
/// </param>
public sealed record OptimizationOutcome(
	OptimizationTrial Chosen,
	IReadOnlyList<OptimizationTrial> Trials,
	RunResult Run,
	int Seed,
	bool WasAlreadySearched);

/// <summary>
/// Searching the numbers a specification declared, on the part of the history it was formed on.
/// </summary>
/// <remarks>
/// The search happens on the development slice and nowhere else. Searching on validation would tune
/// the candidate to the data that is supposed to judge it, and the number that came back would be a
/// measure of the search rather than of the strategy.
///
/// The search ranks; it does not decide. What it picks is then run and measured through exactly the
/// same path as any other run, so the recorded numbers come from one measurement and not from the
/// optimizer's own accounting. What those numbers are worth is nobody's to decide here.
/// </remarks>
public sealed class OptimizationService
{
	/// <summary>Settings that live in one generation.</summary>
	public const int Population = 10;

	/// <summary>Generations the search runs before it stops.</summary>
	public const int Generations = 5;
	private readonly IProjectStore _projects;
	private readonly ICandidateStore _candidates;
	private readonly ISpecStore _specs;
	private readonly IDatasetStore _datasets;
	private readonly IArtifactStore _artifacts;
	private readonly IStrategyOptimizer _optimizer;
	private readonly BacktestService _backtests;
	private readonly IAuditLog _audit;
	private readonly IOperationLog _operations;
	private readonly IClock _clock;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="projects">Where projects are kept.</param>
	/// <param name="candidates">Where candidates are kept.</param>
	/// <param name="specs">Where specifications are kept.</param>
	/// <param name="datasets">Where datasets are kept.</param>
	/// <param name="artifacts">Where assemblies are kept.</param>
	/// <param name="optimizer">What searches the numbers.</param>
	/// <param name="backtests">What runs and measures the chosen setting.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="operations">Where operation keys are remembered.</param>
	/// <param name="clock">Source of the current moment.</param>
	public OptimizationService(
		IProjectStore projects,
		ICandidateStore candidates,
		ISpecStore specs,
		IDatasetStore datasets,
		IArtifactStore artifacts,
		IStrategyOptimizer optimizer,
		BacktestService backtests,
		IAuditLog audit,
		IOperationLog operations,
		IClock clock)
	{
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
		_specs = specs ?? throw new ArgumentNullException(nameof(specs));
		_datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
		_artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
		_optimizer = optimizer ?? throw new ArgumentNullException(nameof(optimizer));
		_backtests = backtests ?? throw new ArgumentNullException(nameof(backtests));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_operations = operations ?? throw new ArgumentNullException(nameof(operations));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>
	/// Searches the numbers of a candidate and measures what it picked.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to search.</param>
	/// <param name="symbol">Symbol to trade, or null for the first of the dataset.</param>
	/// <param name="seed">What the search's randomness starts from.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the search found.</returns>
	public async ValueTask<OptimizationOutcome> SearchAsync(
		ProjectId project,
		CandidateId candidate,
		string symbol,
		int seed,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

		// A search is the most expensive thing an agent can ask for, so a retry of the same request must
		// not buy a second one. Every other tool answers a repeated key from the first result; this one
		// held the log and never used it, and a client that timed out and asked again was charged twice
		// without either call saying so.
		var recorded = await _operations.TryGetAsync(project.Value, operationKey, cancellationToken);

		if (recorded is not null)
		{
			var before = await _backtests.GetAsync(project, RunId.Parse(recorded), cancellationToken);

			// The settings the search looked at are not kept - only the one it chose and the run that
			// measured it - so a replay says so by coming back with no trials rather than by inventing any.
			return new(Chosen(before), [], before, seed, WasAlreadySearched: true);
		}

		var existing = await _projects.OpenAsync(project, cancellationToken);

		if (existing.Dataset.IsEmpty)
			throw new InvalidOperationException("This project has no data, so there is nothing to search over.");

		var built = await _candidates.GetAsync(project, candidate, cancellationToken);
		var manifest = await _datasets.GetManifestAsync(project, existing.Dataset, cancellationToken);

		symbol = string.IsNullOrWhiteSpace(symbol) ? manifest.Symbols[0] : symbol.Trim();

		// A search costs a run for every setting it evaluates, and the ceiling of that is known before it
		// starts. The whole of it is claimed here, in one go and where the number is kept: claiming it
		// afterwards would make an abandoned search free, and claiming it in a copy would let two searches
		// running together cost one.
		var mostItCanCost = Population * Generations;

		if (!await _projects.TryClaimAsync(project, mostItCanCost, candidates: 0, cancellationToken))
		{
			var budget = ResearchBudget.Restore(existing.Budget);

			throw new ResearchBudgetExhaustedException(
				$"A search evaluates up to {mostItCanCost} settings and this project has " +
				$"{budget.RemainingBacktests} backtests and " +
				$"{budget.RemainingWallClock.TotalMinutes:0} minutes of machine time left. Run what you " +
				"already have rather than starting a search that cannot finish.");
		}

		await using var claim = new BudgetClaim(_projects, project, mostItCanCost, candidates: 0);

		var bars = await _datasets.LoadAsync(project, existing.Dataset, symbol, DataSlices.Development, cancellationToken);

		if (bars.Count == 0)
			throw new InvalidOperationException($"The development slice of '{symbol}' holds no bars to search over.");

		var spec = SpecJson.Read((await _specs.GetAsync(project, built.Spec, cancellationToken)).Json);
		var assembly = await _artifacts.ReadAsync(project, built.Assembly, cancellationToken);

		var range = new BarRange(
			_datasets.BarsFolder,
			bars[0].OpenTime,
			bars[^1].OpenTime + manifest.TimeFrame,
			bars.Count);

		// Charged whether or not the search finished, as a backtest's time is: the machine was occupied
		// either way, and a search is the most expensive thing a project can ask for.
		var started = Stopwatch.GetTimestamp();

		IReadOnlyList<OptimizationTrial> trials;

		try
		{
			trials = await _optimizer.SearchAsync(
				new(
					assembly,
					built.ClassName,
					symbol,
					manifest.TimeFrame,
					range,
					BacktestService.StartingEquity,
					BacktestService.PositionSize(spec, symbol, bars[0].Open),
					BacktestService.PriceStep,
					ExecutionCosts.Default,
					Population,
					Generations,
					seed),
				cancellationToken);
		}
		finally
		{
			await _projects.ChargeTimeAsync(project, Stopwatch.GetElapsedTime(started), CancellationToken.None);
		}

		if (trials.Count == 0)
			throw new InvalidOperationException("The search evaluated nothing at all.");

		await _projects.UpdateAsync(existing with { UpdatedAt = _clock.UtcNow }, cancellationToken);

		var chosen = trials[0];

		// Measured through the ordinary path rather than trusted from the search: one measurement, so the
		// numbers a report quotes are the numbers every other run of this project was judged on.
		var (run, _) = await _backtests.RunAsync(
			project,
			candidate,
			DataSlices.Development,
			RunWindow.Whole,
			symbol,
			RunScenario.Baseline,
			chosen.Parameters,
			$"{operationKey}:chosen",
			actor,
			cancellationToken);

		claim.Keep();

		await _operations.RecordAsync(project.Value, operationKey, run.Id.Value, cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.RunCompleted,
			actor,
			$"Searched {built.ClassName} over {trials.Count} settings with seed {seed} and took " +
			$"{Describe(chosen.Parameters)}.",
			built.SourceHash,
			cancellationToken);

		return new(chosen, trials, run, seed, WasAlreadySearched: false);
	}

	/// <summary>
	/// Walks a candidate's declared numbers forward over the development slice: each window searches them on
	/// one stretch and tests the best of them on the stretch right after it.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to walk forward.</param>
	/// <param name="symbol">Symbol to trade, or null for the first of the dataset.</param>
	/// <param name="inSampleDays">Days each window is fitted on.</param>
	/// <param name="outOfSampleDays">Days each window is tested on, and the step between windows.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What each window chose and what that came to on the stretch it had not seen.</returns>
	/// <remarks>
	/// Every window costs a backtest for every setting it searches and one more for the test, so the whole
	/// of it is claimed before anything runs, as a search's is.
	/// </remarks>
	public async ValueTask<WalkForwardOutcome> WalkForwardAsync(
		ProjectId project,
		CandidateId candidate,
		string symbol,
		int inSampleDays,
		int outOfSampleDays,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
		ArgumentOutOfRangeException.ThrowIfLessThan(inSampleDays, 1);
		ArgumentOutOfRangeException.ThrowIfLessThan(outOfSampleDays, 1);

		var recorded = await _operations.TryGetAsync(project.Value, operationKey, cancellationToken);

		if (recorded is not null)
		{
			var first = await _artifacts.ReadAsync(project, ArtifactId.Parse(recorded), cancellationToken);

			return JsonSerializer.Deserialize<WalkForwardOutcome>(first) with { WasAlreadyRun = true };
		}

		var existing = await _projects.OpenAsync(project, cancellationToken);

		if (existing.Dataset.IsEmpty)
			throw new InvalidOperationException("This project has no data, so there is nothing to walk forward over.");

		var built = await _candidates.GetAsync(project, candidate, cancellationToken);
		var manifest = await _datasets.GetManifestAsync(project, existing.Dataset, cancellationToken);

		symbol = string.IsNullOrWhiteSpace(symbol) ? manifest.Symbols[0] : symbol.Trim();

		var bars = await _datasets.LoadAsync(project, existing.Dataset, symbol, DataSlices.Development, cancellationToken);

		if (bars.Count == 0)
			throw new InvalidOperationException($"The development slice of '{symbol}' holds no bars to walk forward over.");

		var spec = SpecJson.Read((await _specs.GetAsync(project, built.Spec, cancellationToken)).Json);

		var inSample = TimeSpan.FromDays(inSampleDays);
		var outOfSample = TimeSpan.FromDays(outOfSampleDays);
		var from = bars[0].OpenTime;
		var to = bars[^1].OpenTime + manifest.TimeFrame;

		// How many windows fit, counted the way they are cut: forward by the tested stretch for as long as a
		// whole window still ends inside the slice.
		var windows = to - from < inSample + outOfSample
			? 0
			: (int)((to - from - inSample - outOfSample).Ticks / outOfSample.Ticks) + 1;

		if (windows == 0)
		{
			throw new ArgumentException(
				$"The development slice of '{symbol}' covers {(to - from).TotalDays:0.#} days, shorter than one " +
				$"window of {inSampleDays} days fitted and {outOfSampleDays} tested.",
				nameof(inSampleDays));
		}

		var settings = spec.Parameters
			.Where(p => p.Optimizable && p.Step > 0 && p.Maximum > p.Minimum)
			.Aggregate(1L, (count, p) => count * ((long)((p.Maximum - p.Minimum) / p.Step) + 1));

		var mostItCanCost = windows * (settings + 1);

		if (mostItCanCost > int.MaxValue
			|| !await _projects.TryClaimAsync(project, (int)mostItCanCost, candidates: 0, cancellationToken))
		{
			var budget = ResearchBudget.Restore(existing.Budget);

			throw new ResearchBudgetExhaustedException(
				$"A walk-forward of {windows} windows over {settings} settings costs up to {mostItCanCost} " +
				$"backtests and this project has {budget.RemainingBacktests} left. Use fewer, longer windows or " +
				"declare fewer numbers to search.");
		}

		await using var claim = new BudgetClaim(_projects, project, (int)mostItCanCost, candidates: 0);

		var assembly = await _artifacts.ReadAsync(project, built.Assembly, cancellationToken);

		var range = new BarRange(
			_datasets.BarsFolder,
			from,
			to,
			bars.Count);

		var started = Stopwatch.GetTimestamp();

		IReadOnlyList<WalkForwardWindowResult> results;

		try
		{
			results = await _optimizer.WalkForwardAsync(
				new(
					assembly,
					built.ClassName,
					symbol,
					manifest.TimeFrame,
					range,
					BacktestService.StartingEquity,
					BacktestService.PositionSize(spec, symbol, bars[0].Open),
					BacktestService.PriceStep,
					ExecutionCosts.Default,
					inSample,
					outOfSample),
				cancellationToken);
		}
		finally
		{
			await _projects.ChargeTimeAsync(project, Stopwatch.GetElapsedTime(started), CancellationToken.None);
		}

		var outcome = new WalkForwardOutcome(
			[.. results.Select(r => new WalkForwardWindowReport(
				r.InSampleFrom,
				r.InSampleTo,
				r.OutOfSampleFrom,
				r.OutOfSampleTo,
				r.Parameters,
				r.InSampleFitness,
				RunMetricsCalculator.Measure(
					r.OutOfSample.Trades,
					r.OutOfSample.Equity,
					BacktestService.StartingEquity,
					r.OutOfSampleTo - r.OutOfSampleFrom,
					r.OutOfSample.ExecutionErrorCount)))],
			WasAlreadyRun: false);

		var stored = await _artifacts.PutAsync(project, JsonSerializer.SerializeToUtf8Bytes(outcome), cancellationToken);

		claim.Keep();

		await _operations.RecordAsync(project.Value, operationKey, stored.Id.Value, cancellationToken);

		await _projects.UpdateAsync(existing with { UpdatedAt = _clock.UtcNow }, cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.RunCompleted,
			actor,
			$"Walked {built.ClassName} forward over {outcome.Windows.Count} windows of {inSampleDays} days fitted " +
			$"and {outOfSampleDays} tested.",
			built.SourceHash,
			cancellationToken);

		return outcome;
	}

	// What the run says about the setting it was given, in the shape the search reports its own trials in.
	private static OptimizationTrial Chosen(RunResult run)
		=> new(
			run.Parameters,
			run.Metrics?.Net.Profit ?? 0m,
			run.Metrics?.Net.Profit ?? 0m,
			run.Metrics?.Risk.MaxDrawdownPercent ?? 0m,
			run.Metrics?.Trades.Count ?? 0);

	private static string Describe(IReadOnlyDictionary<string, decimal> parameters)
		=> parameters.Count == 0
			? "the declared defaults"
			: string.Join(", ", parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
}
