namespace Odysseus.Application;

using System;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;

/// <summary>
/// Allowance taken before the work, and given back when the work did not happen.
/// </summary>
/// <remarks>
/// Claiming first is right: two runs arriving together have to cost two, and that can only be decided
/// where the number is kept. But a claim is a charge, and charging for work nobody received turns an
/// ordinary event - a dropped connection, a restart, someone pressing a key - into a silent cost. The
/// ceiling then arrives earlier than the count of results explains, and nothing on the wire says why.
///
/// So the claim is kept only once the work is a fact in the store. Anything else - the caller walking
/// away, a refusal raised after the claim, a run this server's own machinery broke - gives it back. A
/// run that ran and failed is a fact and is kept: a candidate that cannot be run has been answered, and
/// hiding the attempt would let the same one be proposed again. A run that was never put to the
/// candidate is not a fact about it, and is given back even though it is recorded.
/// </remarks>
internal sealed class BudgetClaim : IAsyncDisposable
{
	private readonly IProjectStore _projects;
	private readonly ProjectId _project;
	private readonly int _backtests;
	private readonly int _candidates;

	private bool _kept;

	/// <summary>
	/// Records what was claimed.
	/// </summary>
	/// <param name="projects">Where the allowance is kept.</param>
	/// <param name="project">Project that was charged.</param>
	/// <param name="backtests">Backtests claimed.</param>
	/// <param name="candidates">Candidates claimed.</param>
	public BudgetClaim(IProjectStore projects, ProjectId project, int backtests, int candidates)
	{
		_projects = projects ?? throw new ArgumentNullException(nameof(projects));
		_project = project;
		_backtests = backtests;
		_candidates = candidates;
	}

	/// <summary>Keeps the claim, because the work it paid for is now on record.</summary>
	public void Keep() => _kept = true;

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_kept)
			return;

		// Not the caller's token. This runs precisely when the caller has gone away, and a release that
		// declined to run for that reason would leave the charge it exists to undo.
		await _projects.ReleaseAsync(_project, _backtests, _candidates, CancellationToken.None);
	}
}
