namespace StockSharp.Odysseus.Application;

/// <summary>
/// Keeps the strategies a project has finished with.
/// </summary>
/// <remarks>
/// The thing being kept is a deliverable in its own folder:
/// somebody will want to open the code, read the specification beside it and see the numbers, months
/// later, on a machine that may not have this server on it at all.
/// </remarks>
public interface ICompletedStore
{
	/// <summary>
	/// Keeps a finished strategy with the code and the words it came from.
	/// </summary>
	/// <param name="project">Project the strategy belongs to.</param>
	/// <param name="strategy">Everything known about it.</param>
	/// <param name="source">The generated C#.</param>
	/// <param name="specJson">The specification it was translated from.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Where it was kept.</returns>
	ValueTask<string> SaveAsync(
		ProjectId project,
		CompletedStrategy strategy,
		string source,
		string specJson,
		CancellationToken cancellationToken);

	/// <summary>
	/// Lists what a project has finished with, oldest first.
	/// </summary>
	/// <param name="project">Project to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The finished strategies.</returns>
	ValueTask<IReadOnlyList<CompletedStrategy>> ListAsync(ProjectId project, CancellationToken cancellationToken);

	/// <summary>
	/// Where a project's finished strategies are kept.
	/// </summary>
	/// <param name="project">Project to ask about.</param>
	/// <returns>The folder, which need not exist yet.</returns>
	string FolderOf(ProjectId project);
}
