namespace Odysseus.Application;

/// <summary>
/// Keeps the candidates of a project.
/// </summary>
public interface ICandidateStore
{
	/// <summary>
	/// Records a candidate.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to record.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	ValueTask AddAsync(ProjectId project, Candidate candidate, CancellationToken cancellationToken);

	/// <summary>
	/// Replaces a candidate that is already recorded.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to save.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <exception cref="CandidateNotFoundException">The candidate does not exist.</exception>
	ValueTask UpdateAsync(ProjectId project, Candidate candidate, CancellationToken cancellationToken);

	/// <summary>
	/// Reads one candidate.
	/// </summary>
	/// <param name="project">Project the candidate belongs to.</param>
	/// <param name="candidate">Candidate to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The candidate.</returns>
	/// <exception cref="CandidateNotFoundException">The candidate does not exist.</exception>
	ValueTask<Candidate> GetAsync(ProjectId project, CandidateId candidate, CancellationToken cancellationToken);

	/// <summary>
	/// Finds a candidate by the hash of its source.
	/// </summary>
	/// <param name="project">Project to search.</param>
	/// <param name="sourceHash">Hash to look for.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The candidate, or <see langword="null"/> when the project has no such source.</returns>
	/// <remarks>
	/// Two specifications that translate to the same source are the same strategy written twice, and
	/// building the second would spend budget to arrive at a candidate that already exists.
	/// </remarks>
	ValueTask<Candidate> FindBySourceAsync(ProjectId project, string sourceHash, CancellationToken cancellationToken);

	/// <summary>
	/// Lists the candidates of a project, oldest first.
	/// </summary>
	/// <param name="project">Project to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The candidates.</returns>
	ValueTask<IReadOnlyList<Candidate>> ListAsync(ProjectId project, CancellationToken cancellationToken);
}
