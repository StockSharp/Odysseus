namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;
using Odysseus.Spec;

/// <summary>
/// What measuring a candidate against the closed history came to.
/// </summary>
/// <param name="Measurement">The numbers from the closed slice.</param>
/// <param name="Runs">The runs they were computed from.</param>
/// <param name="OnOpenData">What the same candidate came to on the data it was allowed to see.</param>
/// <param name="EarlierCandidates">How many candidates of this project spent the slice before it.</param>
/// <param name="TimesMeasuredOnOpenData">How many times it was measured on the open data first.</param>
/// <remarks>
/// The two measurements are returned side by side and left that way. What the distance between them
/// means - a hypothesis that holds, or a search that fitted the open data - is a reading of the numbers,
/// and the reading is the researcher's.
/// </remarks>
public sealed record ClosedMeasurement(
	Measurement Measurement,
	IReadOnlyList<RunId> Runs,
	Measurement OnOpenData,
	int EarlierCandidates,
	int TimesMeasuredOnOpenData);

/// <summary>
/// The one-time measurement against the part of the history nobody has been allowed to see.
/// </summary>
/// <remarks>
/// Everything else in this product can be repeated. This cannot, and that is the whole of its value.
/// A candidate is measured once on data that was closed while it was being formed and tuned; the moment
/// it could be measured twice, the second measurement would be of a candidate adjusted to the first, and
/// the slice would be worth no more than the ones already open.
///
/// So: the same candidate is never measured here twice, and a second candidate measured after the first
/// is allowed but counted — because by then the slice has told someone something, and whoever chose the
/// second candidate knew it.
/// </remarks>
public sealed class ClosedDataService
{
	private readonly ICandidateStore _candidates;
	private readonly ISpecStore _specs;
	private readonly IRunStore _runs;
	private readonly IEvaluationStore _evaluations;
	private readonly IProjectStore _projects;
	private readonly IDatasetStore _datasets;
	private readonly IClosedHistoryLedger _ledger;
	private readonly BacktestService _backtests;
	private readonly IAuditLog _audit;
	private readonly IClock _clock;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="candidates">Where candidates are kept.</param>
	/// <param name="specs">Where specifications are kept.</param>
	/// <param name="runs">Where runs are kept.</param>
	/// <param name="evaluations">Where measurements are kept.</param>
	/// <param name="projects">Where projects are kept.</param>
	/// <param name="datasets">Where datasets are kept.</param>
	/// <param name="ledger">What remembers which closed history has been spent, across every project.</param>
	/// <param name="backtests">What runs a candidate.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="clock">Source of the current moment.</param>
	public ClosedDataService(
		ICandidateStore candidates,
		ISpecStore specs,
		IRunStore runs,
		IEvaluationStore evaluations,
		IProjectStore projects,
		IDatasetStore datasets,
		IClosedHistoryLedger ledger,
		BacktestService backtests,
		IAuditLog audit,
		IClock clock)
	{
		_candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
		_specs = specs ?? throw new ArgumentNullException(nameof(specs));
		_runs = runs ?? throw new ArgumentNullException(nameof(runs));
		_evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
		_ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
		_backtests = backtests ?? throw new ArgumentNullException(nameof(backtests));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>
	/// Measures a candidate once against the closed part of the history.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to measure.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The numbers from the closed slice, beside the ones from the open data.</returns>
	/// <remarks>
	/// What is measured is not the caller's to choose. The instrument and the numbers come from the run
	/// the open-data measurement was taken on: measuring some other setting, on some other symbol, spends
	/// the one slice that could have said something about the candidate that was actually validated.
	/// </remarks>
	public async ValueTask<ClosedMeasurement> MeasureAsync(
		ProjectId project,
		CandidateId candidate,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

		var built = await _candidates.GetAsync(project, candidate, cancellationToken);

		var (earlier, _) = await _evaluations.FindAsync(project, candidate, cancellationToken);

		if (earlier is null)
		{
			throw new InvalidOperationException(
				"This candidate has not been measured on the open data, so there is nothing to compare " +
				"against. Run evaluate_candidate first.");
		}

		// Read against the measurements rather than the runs. Written against the runs, this refused a
		// candidate whose first closed run had merely started - so a client that dropped its connection a
		// second in destroyed the slice for that candidate: counted as spent, no numbers recorded, every
		// later attempt refused as a repeat. Asking again now finishes what was interrupted, and the runs
		// already answered cost nothing to reuse because they answer by fingerprint.
		var measurements = await _evaluations.ListMeasurementsAsync(project, candidate, cancellationToken);

		if (measurements.Any(m => m.HeldOutSlice == DataSlices.Final))
		{
			throw new InvalidOperationException(
				"This candidate has already been measured against the closed part of the history. Measuring " +
				"it again would measure a candidate that has since seen the answer, and there is no second " +
				"slice to fall back on.");
		}

		// Distinct candidates, not runs: one measurement here is five runs, and what matters is how many
		// other candidates the slice has already answered.
		var already = (await _runs.ListAsync(project, default, cancellationToken))
			.Where(r => r.Slice == DataSlices.Final && r.Candidate != candidate)
			.Select(r => r.Candidate)
			.Distinct()
			.ToArray();

		if (built.Status is not (CandidateStatuses.StressTested or CandidateStatuses.Validated))
		{
			throw new InvalidOperationException(
				$"This candidate is {built.Status}. The closed part of the history is spent once, so a " +
				"candidate reaches it only after evaluate_candidate has put it through validation and the " +
				"stressed costs.");
		}

		var existing = await _projects.OpenAsync(project, cancellationToken);
		var manifest = await _datasets.GetManifestAsync(project, existing.Dataset, cancellationToken);

		var validated = (await _runs.ListAsync(project, candidate, cancellationToken))
			.LastOrDefault(r => r.Slice == DataSlices.Validation && r.Window == 0 && r.Status == RunStatuses.Completed)
			?? throw new InvalidOperationException(
				"This candidate has no completed run over the validation slice, so there is no setting to " +
				"measure. Run evaluate_candidate first: what the closed data measures is what validation " +
				"was run on, not something chosen afterwards.");

		var symbol = validated.Symbol;
		var parameters = validated.Parameters;

		// The closed slice runs from the end of validation to the end of the data.
		var closedFrom = manifest.Split.ValidationTo;
		var closedTo = manifest.Split.To;

		var runs = new List<RunId>();

		async Task<RunResult> Run(RunWindow window, RunScenario scenario, string suffix)
		{
			var (run, _) = await _backtests.RunClosedAsync(
				project, candidate, window, symbol, scenario, parameters,
				$"{operationKey}:{suffix}", actor, cancellationToken);

			runs.Add(run.Id);

			if (run.Status != RunStatuses.Completed)
			{
				throw new InvalidOperationException(
					$"The final {suffix} run did not finish, so there is nothing to report: {run.Error}");
			}

			return run;
		}

		// Everything the measurement needs from elsewhere is gathered before the closed data is touched.
		// Discovering a missing piece afterwards would cost the one measurement that cannot be taken
		// again, and cost it for nothing.
		var development = await LatestAsync(project, candidate, DataSlices.Development, cancellationToken);
		var spec = SpecJson.Read((await _specs.GetAsync(project, built.Spec, cancellationToken)).Json);

		// Claimed before the runs, and checked in the same step, so two projects asking at once cannot both
		// pass. This candidate's claim on exactly this stretch is the same spending, which is what lets an
		// interrupted attempt finish. Generated data is exempt: there is no answer in it to have been seen.
		var spent = manifest.IsSynthetic
			? []
			: await _ledger.ClaimAsync(
				new(project, candidate, symbol, manifest.TimeFrame, closedFrom, closedTo, _clock.UtcNow),
				cancellationToken);

		if (spent.Count > 0)
		{
			var first = spent[0];

			// The same candidate can appear under another project, because a candidate is identified by
			// the source it generated, so where it happened is named either way.
			var who = first.Candidate == candidate
				? $"this candidate was measured in project {first.Project}"
				: $"project {first.Project} measured {first.Candidate}";

			throw new InvalidOperationException(
				$"The closed history of '{symbol}' between {closedFrom:yyyy-MM-dd} and {closedTo:yyyy-MM-dd} " +
				$"has already been spent: {who} against {first.From:yyyy-MM-dd} to {first.To:yyyy-MM-dd} on " +
				$"{first.SpentAt:yyyy-MM-dd}. It is closed data because it is answered once. Importing the " +
				"same stretch into another project does not make it unseen - whoever asks now knows what it " +
				"said. Research a different instrument, or a stretch of history that ends before that one " +
				"begins.");
		}

		var final = await Run(RunWindow.Whole, RunScenario.Baseline, "final");
		var stressed = await Run(RunWindow.Whole, RunScenario.Stressed, "finalStressed");

		// Consecutive stretches of the closed slice, not of the slice the candidate was formed on. Asked
		// of the development data these say only that a search covered it evenly; asked of data nobody
		// could tune to, they say whether the edge holds through a stretch of market it never saw.
		var windows = new List<decimal>();

		for (var index = 1; index <= EvaluationService.WalkForwardWindows; index++)
		{
			var window = await Run(
				new(index, EvaluationService.WalkForwardWindows), RunScenario.Baseline, $"finalWindow{index}");

			windows.Add(window.Metrics.Net.ReturnPercent);
		}

		var measurement = new Measurement(
			candidate,
			DataSlices.Final,
			development,
			final.Metrics,
			stressed.Metrics,
			windows,
			EvaluationService.Silences([("heldOut", final), ("heldOutStressed", stressed)]),
			EvaluationService.Shape(spec),
			_clock.UtcNow);

		// Counted before this one is recorded, so it is the number of times the open data was asked and not
		// one more. Numbers reached after a long search over the open slices describe the search, and this
		// is what lets a reader tell.
		var attempts = await _evaluations.CountAsync(project, candidate, cancellationToken);

		await _evaluations.AddAsync(project, measurement, runs, cancellationToken);

		var moved = built;

		// A candidate that reached here through evaluate_candidate is already stress tested; one that was
		// only validated still has that step to take before the lifecycle will let it come here.
		foreach (var stage in new[] { CandidateStatuses.StressTested, CandidateStatuses.FinalChecked })
		{
			if (CandidateLifecycle.CanTransition(moved.Status, stage))
				moved = moved.WithStatus(stage, _clock.UtcNow);
		}

		await _candidates.UpdateAsync(project, moved, cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.FinalHoldoutConsumed,
			actor,
			$"Measured {built.ClassName} once against the closed slice of '{symbol}', {closedFrom:yyyy-MM-dd} to " +
			$"{closedTo:yyyy-MM-dd}: net " +
			$"{final.Metrics.Net.Profit:F2} from {final.Metrics.Trades.Count} trades, stressed " +
			$"{stressed.Metrics.Net.Profit:F2}.",
			built.SourceHash,
			cancellationToken);

		return new(measurement, runs, earlier, already.Length, attempts);
	}

	/// <summary>
	/// Says whether the closed part of the history has been spent, without saying where it is.
	/// </summary>
	/// <param name="project">Project to ask about.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The candidates that have been measured against it.</returns>
	public async ValueTask<IReadOnlyList<CandidateId>> SpentOnAsync(
		ProjectId project,
		CancellationToken cancellationToken)
		=> [.. (await _runs.ListAsync(project, default, cancellationToken))
			.Where(r => r.Slice == DataSlices.Final)
			.Select(r => r.Candidate)
			.Distinct()];

	private async ValueTask<RunMetrics> LatestAsync(
		ProjectId project,
		CandidateId candidate,
		DataSlices slice,
		CancellationToken cancellationToken)
	{
		var run = (await _runs.ListAsync(project, candidate, cancellationToken))
			.LastOrDefault(r => r.Slice == slice && r.Window == 0 && r.Status == RunStatuses.Completed)
			?? throw new InvalidOperationException(
				$"This candidate has no completed run over the {slice} slice, which the closed data is compared against.");

		return run.Metrics;
	}
}
