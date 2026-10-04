namespace Odysseus.Application;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;

/// <summary>
/// Keeps the runs of a project.
/// </summary>
public interface IRunStore
{
	/// <summary>
	/// Records a run.
	/// </summary>
	/// <param name="project">Project the run belongs to.</param>
	/// <param name="run">Run to record.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	ValueTask AddAsync(ProjectId project, RunResult run, CancellationToken cancellationToken);

	/// <summary>
	/// Reads one run.
	/// </summary>
	/// <param name="project">Project the run belongs to.</param>
	/// <param name="run">Run to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The run.</returns>
	/// <exception cref="RunNotFoundException">The run does not exist.</exception>
	ValueTask<RunResult> GetAsync(ProjectId project, RunId run, CancellationToken cancellationToken);

	/// <summary>
	/// Finds a completed run by what made it that run.
	/// </summary>
	/// <param name="project">Project to search.</param>
	/// <param name="fingerprint">Fingerprint to look for.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The run, or <see langword="null"/> when the project has not run it.</returns>
	ValueTask<RunResult> FindAsync(ProjectId project, string fingerprint, CancellationToken cancellationToken);

	/// <summary>
	/// Lists the runs of a project, oldest first.
	/// </summary>
	/// <param name="project">Project to list.</param>
	/// <param name="candidate">Candidate to list the runs of, or the default value for all of them.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The runs.</returns>
	ValueTask<IReadOnlyList<RunResult>> ListAsync(
		ProjectId project,
		CandidateId candidate,
		CancellationToken cancellationToken);
}
