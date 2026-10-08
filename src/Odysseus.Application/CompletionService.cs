namespace StockSharp.Odysseus.Application;

using System.Linq;
using System.Text;

using StockSharp.Odysseus.Spec;

/// <summary>
/// A strategy that has just been declared finished, and where it was kept.
/// </summary>
/// <param name="Strategy">Everything the server knows about it.</param>
/// <param name="Folder">Where the code, the specification and the numbers were written.</param>
/// <param name="Files">The files written, by name.</param>
public sealed record Completion(CompletedStrategy Strategy, string Folder, IReadOnlyList<string> Files);

/// <summary>
/// Keeping a strategy somebody has decided is finished.
/// </summary>
/// <remarks>
/// Everywhere else the server measures and says nothing about what the numbers are worth. This is the
/// one place a conclusion is recorded, and it is recorded because it arrived from outside: the agent, or
/// the person driving it, read the runs and said this is the strategy. The server's part is to write
/// down what was concluded, what it was concluded about, and everything needed to check it later.
///
/// It refuses a candidate the closed data has never seen. That measurement is the only one nobody could
/// have tuned to, and a strategy called finished without it was finished on a search.
/// </remarks>
public sealed class CompletionService
{
	private readonly ICandidateStore _candidates;
	private readonly ISpecStore _specs;
	private readonly IRunStore _runs;
	private readonly IEvaluationStore _evaluations;
	private readonly IProjectStore _projects;
	private readonly IDatasetStore _datasets;
	private readonly IArtifactStore _artifacts;
	private readonly ICompletedStore _completed;
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
	/// <param name="artifacts">Where the generated source is kept.</param>
	/// <param name="completed">Where finished strategies are kept.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="clock">Source of the current moment.</param>
	public CompletionService(
		ICandidateStore candidates,
		ISpecStore specs,
		IRunStore runs,
		IEvaluationStore evaluations,
		IProjectStore projects,
		IDatasetStore datasets,
		IArtifactStore artifacts,
		ICompletedStore completed,
		IAuditLog audit,
		IClock clock)
	{
		_candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
		_specs = specs ?? throw new ArgumentNullException(nameof(specs));
		_runs = runs ?? throw new ArgumentNullException(nameof(runs));
		_evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
		_artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
		_completed = completed ?? throw new ArgumentNullException(nameof(completed));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>
	/// Records that a candidate is finished and keeps it with everything behind it.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate that is finished.</param>
	/// <param name="notes">Why whoever is doing the research decided it is finished.</param>
	/// <param name="actor">Who is deciding.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What was kept and where.</returns>
	/// <exception cref="ArgumentException">No reason was given.</exception>
	/// <exception cref="InvalidOperationException">The candidate is not one that can be finished.</exception>
	public async ValueTask<Completion> CompleteAsync(
		ProjectId project,
		CandidateId candidate,
		string notes,
		Actors actor,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(notes);

		var built = await _candidates.GetAsync(project, candidate, cancellationToken);

		if (built.Status == CandidateStatuses.Completed)
		{
			throw new InvalidOperationException(
				"This candidate has already been finished with, and what was concluded about it is written " +
				"down. Build another candidate to try something else.");
		}

		if (built.Status is not (CandidateStatuses.FinalChecked or CandidateStatuses.PaperRunning))
		{
			throw new InvalidOperationException(
				$"This candidate is {built.Status}. A strategy is finished only after the closed part of the " +
				"history has been measured, because that is the one measurement nobody could have tuned to: " +
				"run measure_on_closed_data, read what it says, and decide then.");
		}

		var measurements = await _evaluations.ListMeasurementsAsync(project, candidate, cancellationToken);

		var onClosedData = measurements.LastOrDefault(m => m.HeldOutSlice == DataSlices.Final)
			?? throw new InvalidOperationException(
				"This candidate has no measurement against the closed part of the history, so there is " +
				"nothing to finish with. Run measure_on_closed_data first.");

		var onOpenData = measurements.LastOrDefault(m => m.HeldOutSlice != DataSlices.Final) ?? onClosedData;

		var all = await _runs.ListAsync(project, candidate, cancellationToken);

		var closedRun = all
			.Where(r => r.Slice == DataSlices.Final && r.Window == 0 && r.Status == RunStatuses.Completed)
			.OrderByDescending(r => r.FinishedAt)
			.FirstOrDefault()
			?? throw new InvalidOperationException(
				"This candidate has no completed run over the closed slice, so there is nothing to keep.");

		var spec = await _specs.GetAsync(project, built.Spec, cancellationToken);
		var parsed = SpecJson.Read(spec.Json);

		var existing = await _projects.OpenAsync(project, cancellationToken);
		var manifest = await _datasets.GetManifestAsync(project, existing.Dataset, cancellationToken);

		var source = Encoding.UTF8.GetString(await _artifacts.ReadAsync(project, built.Source, cancellationToken));

		var strategy = new CompletedStrategy
		{
			Project = project,
			Candidate = candidate,
			Spec = built.Spec,
			SpecRevision = spec.Revision,
			Name = parsed.Name,
			Thesis = parsed.Thesis,
			ClassName = built.ClassName,
			SourceHash = built.SourceHash,
			AssemblyHash = built.AssemblyHash,
			TranslatorVersion = built.TranslatorVersion,
			Symbol = closedRun.Symbol,
			TimeFrame = manifest.TimeFrame,
			Parameters = closedRun.Parameters,
			Dataset = manifest.Id,
			DatasetSource = manifest.Source,
			IsSyntheticData = manifest.IsSynthetic,
			Split = manifest.Split,
			Runs = [.. all.Where(r => r.Status == RunStatuses.Completed).Select(Describe)],
			OnOpenData = onOpenData,
			OnClosedData = onClosedData,
			TimesMeasuredOnOpenData = measurements.Count(m => m.HeldOutSlice != DataSlices.Final),
			Notes = notes.Trim(),
			CompletedBy = actor,
			CompletedAt = _clock.UtcNow,
		};

		var folder = await _completed.SaveAsync(project, strategy, source, spec.Json, cancellationToken);

		await _candidates.UpdateAsync(
			project, built.WithStatus(CandidateStatuses.Completed, _clock.UtcNow), cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.StrategyCompleted,
			actor,
			$"Finished with {built.ClassName} on {strategy.Symbol}: {strategy.Notes}",
			built.SourceHash,
			cancellationToken);

		return new(strategy, folder, [$"{built.ClassName}.cs", "spec.json", "strategy.json"]);
	}

	/// <summary>
	/// Lists what a project has finished with.
	/// </summary>
	/// <param name="project">Project to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The finished strategies, oldest first.</returns>
	public ValueTask<IReadOnlyList<CompletedStrategy>> ListAsync(
		ProjectId project,
		CancellationToken cancellationToken)
		=> _completed.ListAsync(project, cancellationToken);

	/// <summary>
	/// Where a project's finished strategies are kept.
	/// </summary>
	/// <param name="project">Project to ask about.</param>
	/// <returns>The folder, which need not exist yet.</returns>
	public string FolderOf(ProjectId project) => _completed.FolderOf(project);

	private static CompletedRun Describe(RunResult run)
		=> new(
			run.Id,
			run.Slice,
			run.Window,
			run.Symbol,
			run.Scenario,
			run.Parameters,
			run.Metrics,
			run.BarsProcessed,
			run.FinishedAt);
}
