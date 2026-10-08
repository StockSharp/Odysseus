namespace StockSharp.Odysseus.Application;

using System.Linq;

using StockSharp.Odysseus.Spec;

/// <summary>
/// Keeps the measurements made of the candidates of a project.
/// </summary>
public interface IEvaluationStore
{
	/// <summary>
	/// Records a measurement.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="measurement">Measurement to record.</param>
	/// <param name="runs">Runs it was computed from.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	ValueTask AddAsync(
		ProjectId project,
		Measurement measurement,
		IReadOnlyList<RunId> runs,
		CancellationToken cancellationToken);

	/// <summary>
	/// Reads the latest measurement of a candidate.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The measurement and the runs behind it, or the default when there is none.</returns>
	ValueTask<(Measurement Measurement, IReadOnlyList<RunId> Runs)> FindAsync(
		ProjectId project,
		CandidateId candidate,
		CancellationToken cancellationToken);

	/// <summary>
	/// Lists every measurement of a candidate, oldest first.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The measurements.</returns>
	ValueTask<IReadOnlyList<Measurement>> ListMeasurementsAsync(
		ProjectId project,
		CandidateId candidate,
		CancellationToken cancellationToken);

	/// <summary>
	/// How many times a candidate has been measured.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to count.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The number of measurements recorded about it.</returns>
	/// <remarks>
	/// Measuring the same candidate over and over, with the numbers nudged each time, is a search - and a
	/// search over the data that was meant to check searches. The count says how many times the project
	/// looked, which is something a reader of the result is entitled to know.
	/// </remarks>
	ValueTask<int> CountAsync(ProjectId project, CandidateId candidate, CancellationToken cancellationToken);
}

/// <summary>
/// What measuring a candidate produced.
/// </summary>
/// <param name="Measurement">The numbers.</param>
/// <param name="Runs">The runs they were computed from.</param>
/// <param name="WasAlreadyMeasured">Whether this measurement was already on record.</param>
/// <param name="TimesMeasured">
/// How many times this candidate has been measured, counting this one. Numbers arrived at on the
/// fortieth pass describe a search rather than a hypothesis, and this is what says which happened.
/// </param>
public sealed record MeasurementResult(
	Measurement Measurement,
	IReadOnlyList<RunId> Runs,
	bool WasAlreadyMeasured,
	int TimesMeasured);

/// <summary>
/// Putting a candidate through a fixed set of runs and reporting what they came to.
/// </summary>
/// <remarks>
/// The runs are fixed rather than chosen: the same six every time, in the same order, under the same
/// costs. An agent that could choose which evidence its candidate is measured on would choose the
/// evidence that suits it, and the difference between a research tool and a plausible one is exactly
/// that choice being taken away.
///
/// Nothing here judges. The numbers come back and what they are worth is the reader's to decide; a
/// threshold written into a server is somebody else's opinion arriving as though it were arithmetic.
///
/// The closed part of the history is not touched here. This is measured on data the agent has been able
/// to see; the closed slice is a separate step, used once, and its whole value lies in not having been
/// used yet.
/// </remarks>
public sealed class EvaluationService
{
	/// <summary>How many consecutive windows the development slice is cut into for walk-forward.</summary>
	public const int WalkForwardWindows = 3;
	private readonly IProjectStore _projects;
	private readonly ICandidateStore _candidates;
	private readonly ISpecStore _specs;
	private readonly IEvaluationStore _evaluations;
	private readonly BacktestService _backtests;
	private readonly IAuditLog _audit;
	private readonly IClock _clock;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="projects">Where projects are kept.</param>
	/// <param name="candidates">Where candidates are kept.</param>
	/// <param name="specs">Where specifications are kept.</param>
	/// <param name="evaluations">Where measurements are kept.</param>
	/// <param name="backtests">What runs a candidate.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="clock">Source of the current moment.</param>
	public EvaluationService(
		IProjectStore projects,
		ICandidateStore candidates,
		ISpecStore specs,
		IEvaluationStore evaluations,
		BacktestService backtests,
		IAuditLog audit,
		IClock clock)
	{
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
		_specs = specs ?? throw new ArgumentNullException(nameof(specs));
		_evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
		_backtests = backtests ?? throw new ArgumentNullException(nameof(backtests));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>
	/// Runs the fixed set and reports what it came to.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to measure.</param>
	/// <param name="symbol">Symbol to trade, or null for the first of the dataset.</param>
	/// <param name="parameters">Values to set on the strategy, or null for the ones it declares.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The measurement.</returns>
	public async ValueTask<MeasurementResult> MeasureAsync(
		ProjectId project,
		CandidateId candidate,
		string symbol,
		IReadOnlyDictionary<string, decimal> parameters,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

		var built = await _candidates.GetAsync(project, candidate, cancellationToken);
		var runs = new List<RunId>();

		async Task<RunResult> Run(DataSlices slice, RunWindow window, RunScenario scenario, string suffix)
		{
			var (run, _) = await _backtests.RunAsync(
				project, candidate, slice, window, symbol, scenario, parameters,
				$"{operationKey}:{suffix}", actor, cancellationToken);

			runs.Add(run.Id);

			if (run.Status != RunStatuses.Completed)
			{
				throw new InvalidOperationException(
					$"The {suffix} run of this candidate did not finish, so it cannot be judged: {run.Error}");
			}

			return run;
		}

		var development = await Run(DataSlices.Development, RunWindow.Whole, RunScenario.Baseline, "development");
		var validation = await Run(DataSlices.Validation, RunWindow.Whole, RunScenario.Baseline, "validation");
		var stressed = await Run(DataSlices.Validation, RunWindow.Whole, RunScenario.Stressed, "stressed");

		var windows = new List<decimal>();

		for (var index = 1; index <= WalkForwardWindows; index++)
		{
			var window = await Run(
				DataSlices.Development, new(index, WalkForwardWindows), RunScenario.Baseline, $"window{index}");

			windows.Add(window.Metrics.Net.ReturnPercent);
		}

		var spec = SpecJson.Read((await _specs.GetAsync(project, built.Spec, cancellationToken)).Json);

		var measurement = new Measurement(
			candidate,
			DataSlices.Validation,
			development.Metrics,
			validation.Metrics,
			stressed.Metrics,
			windows,
			Silences([("development", development), ("heldOut", validation), ("heldOutStressed", stressed)]),
			Shape(spec),
			_clock.UtcNow);

		var times = await _evaluations.CountAsync(project, candidate, cancellationToken) + 1;

		await _evaluations.AddAsync(project, measurement, runs, cancellationToken);

		await Advance(project, candidate, cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.CandidateEvaluated,
			actor,
			$"Measured {built.ClassName} over six runs: validation {measurement.HeldOut.Net.Profit:F2} " +
			$"from {measurement.HeldOut.Trades.Count} trades, stressed " +
			$"{measurement.HeldOutStressed.Net.Profit:F2}, {measurement.PositiveWindows} of " +
			$"{measurement.WalkForwardReturns.Count} windows positive.",
			built.SourceHash,
			cancellationToken);

		return new(measurement, runs, false, times);
	}

	/// <summary>
	/// Reads the latest measurement of a candidate.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The measurement, or null when the candidate has not been measured.</returns>
	public async ValueTask<MeasurementResult> FindAsync(
		ProjectId project,
		CandidateId candidate,
		CancellationToken cancellationToken)
	{
		var (measurement, runs) = await _evaluations.FindAsync(project, candidate, cancellationToken);

		return measurement is null
			? null
			: new(measurement, runs, true, await _evaluations.CountAsync(project, candidate, cancellationToken));
	}

	// A run that traded nothing already knows whether the rules never fired or the orders were too large
	// to fill, and those are opposite findings that produce the same row of zeros. Carrying the sentence
	// into the measurement is the difference between reporting a result and reporting a blank.
	internal static IReadOnlyList<SilentSlice> Silences(IReadOnlyList<(string Slice, RunResult Run)> parts)
		=> [.. parts
			.Where(p => p.Run.Metrics is { Trades.Count: 0 } && p.Run.Diagnosis is { Length: > 0 })
			.Select(p => new SilentSlice(p.Slice, p.Run.Diagnosis))];

	// Every rule, indicator and parameter is another way for a search to find something that is not
	// there, so how many there are is reported beside what they produced.
	internal static SpecificationShape Shape(StrategySpec spec)
		=> new(
			spec.Entries.Count + spec.Exits.Count,
			spec.AllExpressions
				.SelectMany(Flatten)
				.OfType<IndicatorRef>()
				.Select(i => i.Name)
				.Distinct(StringComparer.Ordinal)
				.Count(),
			spec.Parameters.Count);

	private static IEnumerable<Expression> Flatten(Expression expression)
	{
		yield return expression;

		foreach (var child in expression.Children)
		{
			foreach (var nested in Flatten(child))
				yield return nested;
		}
	}

	// A measured candidate has been through validation and the stressed costs, so it says so. Whether it
	// is any good is not recorded here, because the server does not decide that.
	private async ValueTask Advance(ProjectId project, CandidateId candidate, CancellationToken cancellationToken)
	{
		// Re-read: the runs above moved the candidate from compiled to backtested, so the copy this method
		// was handed before them is a stage behind.
		var current = await _candidates.GetAsync(project, candidate, cancellationToken);
		var moved = current;

		foreach (var stage in new[] { CandidateStatuses.Validated, CandidateStatuses.StressTested })
		{
			if (CandidateLifecycle.CanTransition(moved.Status, stage))
				moved = moved.WithStatus(stage, _clock.UtcNow);
		}

		if (moved.Status != current.Status)
			await _candidates.UpdateAsync(project, moved, cancellationToken);
	}
}
