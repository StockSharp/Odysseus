namespace StockSharp.Odysseus.Application;

/// <summary>
/// Keeps research projects across restarts.
/// </summary>
/// <remarks>
/// Each project is self-contained on disk, so it can be exported, moved to another machine and opened
/// there without the rest of the installation coming along.
/// </remarks>
public interface IProjectStore
{
	/// <summary>
	/// Saves a newly created project.
	/// </summary>
	/// <param name="project">Project to save.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>A task that completes when the project is on disk.</returns>
	ValueTask CreateAsync(ResearchProject project, CancellationToken cancellationToken);

	/// <summary>
	/// Saves changes to an existing project.
	/// </summary>
	/// <param name="project">Project to save.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>A task that completes when the change is on disk.</returns>
	/// <exception cref="ProjectNotFoundException">The project does not exist.</exception>
	ValueTask UpdateAsync(ResearchProject project, CancellationToken cancellationToken);

	/// <summary>
	/// Opens a project.
	/// </summary>
	/// <param name="id">Project to open.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The project.</returns>
	/// <exception cref="ProjectNotFoundException">The project does not exist.</exception>
	ValueTask<ResearchProject> OpenAsync(ProjectId id, CancellationToken cancellationToken);

	/// <summary>
	/// Lists the projects that exist.
	/// </summary>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The projects, most recently changed first.</returns>
	ValueTask<IReadOnlyList<ResearchProject>> ListAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Claims allowance, all of it or none of it.
	/// </summary>
	/// <param name="project">Project to charge.</param>
	/// <param name="backtests">Backtests to claim.</param>
	/// <param name="candidates">Candidates to claim.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns><see langword="true"/> when the allowance covered it.</returns>
	/// <remarks>
	/// The claim happens where the number is kept rather than in a copy of it. Read it, add to it and
	/// write it back and two calls arriving together both read the same figure and both write the same
	/// one back: the second spends nothing, and the ceiling that is the only thing stopping an agent's
	/// loop stops counting exactly when the loop runs hardest.
	///
	/// Machine time is checked here too. It is spent by work that has finished rather than claimed in
	/// advance - nothing knows how long a run will take before it takes it - so what this refuses is
	/// starting anything more once the time is gone.
	/// </remarks>
	ValueTask<bool> TryClaimAsync(
		ProjectId project,
		int backtests,
		int candidates,
		CancellationToken cancellationToken);

	/// <summary>
	/// Gives back allowance that was claimed for work which did not happen.
	/// </summary>
	/// <param name="project">Project to credit.</param>
	/// <param name="backtests">Backtests to give back.</param>
	/// <param name="candidates">Candidates to give back.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <remarks>
	/// The mirror of the claim, and in the same place for the same reason: read the figure, subtract from
	/// it and write it back, and two releases arriving together give back one between them.
	///
	/// It cannot take the count below what has been spent. A release is only ever the undoing of a claim
	/// this call made a moment ago, so a count that would go negative means the release is being asked
	/// for twice, and the second one is a mistake rather than a credit.
	/// </remarks>
	ValueTask ReleaseAsync(
		ProjectId project,
		int backtests,
		int candidates,
		CancellationToken cancellationToken);

	/// <summary>
	/// Charges machine time that has been spent.
	/// </summary>
	/// <param name="project">Project to charge.</param>
	/// <param name="elapsed">Time the work took.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	ValueTask ChargeTimeAsync(ProjectId project, TimeSpan elapsed, CancellationToken cancellationToken);
}
