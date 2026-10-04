namespace Odysseus.Application;

/// <summary>
/// Keeps what each project researches: the description of its dataset, and the bars behind it in the
/// market-data storage every project shares.
/// </summary>
public interface IDatasetStore
{
	/// <summary>
	/// Folder of the shared market-data storage, which is what a run in another process is pointed at.
	/// </summary>
	/// <remarks>
	/// The storage holds every slice of every imported range. What a run reads is bounded by the range it
	/// is given, not by the folder.
	/// </remarks>
	string BarsFolder { get; }

	/// <summary>
	/// Writes a dataset's bars into the shared storage and keeps its description with the project.
	/// </summary>
	/// <param name="project">Project the dataset belongs to.</param>
	/// <param name="dataset">Dataset to save.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>A task that completes when the dataset is on disk.</returns>
	ValueTask SaveAsync(ProjectId project, ImportedDataset dataset, CancellationToken cancellationToken);

	/// <summary>
	/// Reads the description of a dataset.
	/// </summary>
	/// <param name="project">Project the dataset belongs to.</param>
	/// <param name="dataset">Dataset to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The manifest.</returns>
	/// <exception cref="DatasetNotFoundException">The dataset does not exist.</exception>
	ValueTask<DatasetManifest> GetManifestAsync(ProjectId project, DatasetId dataset, CancellationToken cancellationToken);

	/// <summary>
	/// Reads the bars of one symbol within one slice.
	/// </summary>
	/// <param name="project">Project the dataset belongs to.</param>
	/// <param name="dataset">Dataset to read.</param>
	/// <param name="symbol">Symbol to read.</param>
	/// <param name="slice">Slice to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The bars, oldest first.</returns>
	/// <exception cref="DatasetNotFoundException">The dataset does not exist.</exception>
	ValueTask<IReadOnlyList<Candle>> LoadAsync(
		ProjectId project,
		DatasetId dataset,
		string symbol,
		DataSlices slice,
		CancellationToken cancellationToken);
}
