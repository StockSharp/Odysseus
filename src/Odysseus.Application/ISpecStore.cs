namespace Odysseus.Application;

/// <summary>
/// One recorded version of a strategy specification.
/// </summary>
/// <param name="Id">Identity of this version.</param>
/// <param name="Revision">Position in the project's sequence of specifications, starting at one.</param>
/// <param name="Json">The specification as it was received.</param>
/// <param name="Hash">Hash of the text, which every candidate built from it carries.</param>
/// <param name="Author">Who wrote it.</param>
/// <param name="CreatedAt">When it was recorded, in UTC.</param>
/// <remarks>
/// A specification is never edited. A change is a new revision with its own identity, so a candidate
/// always points at the exact words it was built from, and a report can be read years later without
/// wondering whether the specification behind it has since been reworded.
/// </remarks>
public sealed record SpecRevision(
	SpecId Id,
	int Revision,
	string Json,
	string Hash,
	Actors Author,
	DateTime CreatedAt);

/// <summary>
/// Keeps the specifications of a project.
/// </summary>
public interface ISpecStore
{
	/// <summary>
	/// Records a specification as the next revision.
	/// </summary>
	/// <param name="project">Project the specification belongs to.</param>
	/// <param name="json">The specification as it was received.</param>
	/// <param name="author">Who wrote it.</param>
	/// <param name="now">Moment of recording, in UTC.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The recorded revision.</returns>
	ValueTask<SpecRevision> AddAsync(
		ProjectId project,
		string json,
		Actors author,
		DateTime now,
		CancellationToken cancellationToken);

	/// <summary>
	/// Reads one specification.
	/// </summary>
	/// <param name="project">Project the specification belongs to.</param>
	/// <param name="spec">Specification to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The revision.</returns>
	/// <exception cref="SpecNotFoundException">The specification does not exist.</exception>
	ValueTask<SpecRevision> GetAsync(ProjectId project, SpecId spec, CancellationToken cancellationToken);

	/// <summary>
	/// Lists the specifications of a project, oldest first.
	/// </summary>
	/// <param name="project">Project to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The revisions.</returns>
	ValueTask<IReadOnlyList<SpecRevision>> ListAsync(ProjectId project, CancellationToken cancellationToken);
}
