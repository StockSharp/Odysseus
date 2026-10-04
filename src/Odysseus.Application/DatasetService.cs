namespace Odysseus.Application;

using System.Linq;

/// <summary>
/// Importing data and telling an agent what it may know about it.
/// </summary>
public sealed class DatasetService
{
	private readonly IProjectStore _projects;
	private readonly IDatasetStore _datasets;
	private readonly IRunStore _runs;
	private readonly IAuditLog _audit;
	private readonly IOperationLog _operations;
	private readonly IClock _clock;

	/// <summary>
	/// Creates the service.
	/// </summary>
	/// <param name="projects">Where projects are kept.</param>
	/// <param name="datasets">Where datasets are kept.</param>
	/// <param name="runs">Where runs are kept, which is what knows whether the closed slice was spent.</param>
	/// <param name="audit">Where the permanent record is kept.</param>
	/// <param name="operations">Where operation keys are remembered.</param>
	/// <param name="clock">Source of the current moment.</param>
	public DatasetService(
		IProjectStore projects,
		IDatasetStore datasets,
		IRunStore runs,
		IAuditLog audit,
		IOperationLog operations,
		IClock clock)
	{
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
		_runs = runs ?? throw new ArgumentNullException(nameof(runs));
		_audit = audit ?? throw new ArgumentNullException(nameof(audit));
		_operations = operations ?? throw new ArgumentNullException(nameof(operations));
		_clock = clock ?? throw new ArgumentNullException(nameof(clock));
	}

	/// <summary>
	/// Imports the bundled generated dataset, so the pipeline can be exercised without a broker account.
	/// </summary>
	/// <param name="project">Project to import into.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="actor">Who is asking.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The description of what was imported.</returns>
	public async ValueTask<DatasetManifest> ImportDemoAsync(
		ProjectId project,
		string operationKey,
		Actors actor,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);

		var recorded = await _operations.TryGetAsync(project.Value, operationKey, cancellationToken);

		if (recorded is not null)
			return await _datasets.GetManifestAsync(project, DatasetId.Parse(recorded), cancellationToken);

		var existing = await _projects.OpenAsync(project, cancellationToken);
		var imported = DemoDataset.Build();

		var claimed = await _operations.RecordAsync(project.Value, operationKey, imported.Manifest.Id.Value, cancellationToken);

		if (claimed != imported.Manifest.Id.Value)
			return await _datasets.GetManifestAsync(project, DatasetId.Parse(claimed), cancellationToken);

		await _datasets.SaveAsync(project, imported, cancellationToken);
		await _projects.UpdateAsync(existing.WithDataset(imported.Manifest.Id, _clock.UtcNow), cancellationToken);

		await _audit.AppendAsync(
			project,
			AuditEventTypes.DatasetImported,
			actor,
			$"Imported generated bars over {imported.Manifest.Symbols.Count} symbols, {imported.Manifest.Records} in all, " +
			$"from {imported.Manifest.Split.From:yyyy-MM-dd HH:mm} to {imported.Manifest.Split.To:yyyy-MM-dd HH:mm}.",
			"-",
			cancellationToken);

		return imported.Manifest;
	}

	/// <summary>
	/// Reads the description of the dataset a project is working against.
	/// </summary>
	/// <param name="project">Project to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The manifest.</returns>
	/// <exception cref="InvalidOperationException">The project has no dataset yet.</exception>
	public async ValueTask<DatasetManifest> GetManifestAsync(ProjectId project, CancellationToken cancellationToken)
	{
		var existing = await _projects.OpenAsync(project, cancellationToken);

		if (existing.Dataset.IsEmpty)
			throw new InvalidOperationException("This project has no dataset yet. Import one first, for example with import_demo_dataset.");

		return await _datasets.GetManifestAsync(project, existing.Dataset, cancellationToken);
	}

	/// <summary>
	/// Describes how the data is divided, and whether the closed slice has been measured yet.
	/// </summary>
	/// <param name="project">Project to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The division, and whether the closed slice is spent.</returns>
	public async ValueTask<(DatasetSplit Split, bool IsClosedSliceSpent)> DescribeSplitAsync(
		ProjectId project,
		CancellationToken cancellationToken)
	{
		var manifest = await GetManifestAsync(project, cancellationToken);

		var spent = (await _runs.ListAsync(project, default, cancellationToken))
			.Any(r => r.Slice == DataSlices.Final);

		return (manifest.Split, spent);
	}
}
