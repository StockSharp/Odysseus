namespace Odysseus.Persistence.Tests;

using System.Threading;

/// <summary>
/// Spending a project's allowance.
/// </summary>
/// <remarks>
/// The allowance is the only thing that stops the agent's loop, and the loop is exactly when several
/// calls arrive at once. Read the figure, add to it and write it back, and two calls arriving together
/// read the same number and write the same number: the second spends nothing, and the ceiling stops
/// counting precisely when it is most needed.
///
/// So the interesting case is not a claim inside the allowance but a crowd of them at its edge. Claims
/// that fit prove the addition is atomic; only claims competing for the last of the allowance prove the
/// limit itself holds, and those are the ones a store with the comparison in C# rather than in the
/// statement gets wrong.
/// </remarks>
[TestClass]
public class ProjectBudgetTests : OdysseusTestBase
{
	private string _root;
	private SqliteProjectStore _store;

	/// <summary>Builds a store over temporary storage.</summary>
	[TestInitialize]
	public void CreateStore()
	{
		_root = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("n"));
		_store = new SqliteProjectStore(_root);
	}

	/// <summary>Releases storage.</summary>
	[TestCleanup]
	public void DeleteStore()
	{
		_store?.Dispose();

		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>A claim is taken, and what is left says so.</summary>
	[TestMethod]
	public async Task AClaimIsTaken()
	{
		var project = await NewProjectAsync(backtests: 10);

		IsTrue(await _store.TryClaimAsync(project, 3, 0, CancellationToken), "the claim was refused.");

		var budget = ResearchBudget.Restore((await _store.OpenAsync(project, CancellationToken)).Budget);

		AreEqual(7, budget.RemainingBacktests);
	}

	/// <summary>Claims arriving together each cost what they claim.</summary>
	[TestMethod]
	public async Task ClaimsThatArriveTogetherEachCost()
	{
		var project = await NewProjectAsync(backtests: 100);

		var claims = Enumerable.Range(0, 20)
			.Select(_ => _store.TryClaimAsync(project, 1, 0, CancellationToken).AsTask())
			.ToArray();

		await Task.WhenAll(claims);

		IsTrue(claims.All(c => c.Result), "a claim well inside the allowance was refused.");

		var budget = ResearchBudget.Restore((await _store.OpenAsync(project, CancellationToken)).Budget);

		AreEqual(80, budget.RemainingBacktests, "twenty claims did not cost twenty.");
	}

	/// <summary>
	/// More callers than there are backtests left, all of them inside the claim at once: exactly the ones
	/// the project can afford are granted, and the project ends its research at the number it was granted.
	/// </summary>
	/// <remarks>
	/// This is the case the ceiling exists for. Twenty claims out of a hundred say the addition is atomic
	/// and nothing about the limit; thirty-two callers competing for four backtests say whether the limit
	/// is a limit. Take the comparison out of the statement and decide it in C# - read the figure, find
	/// room in it, write the sum back - and each of these reads the same figure, each finds the same room,
	/// and the project runs eight times the research it was allowed to.
	/// </remarks>
	[TestMethod]
	public async Task ClaimsRacingAtTheCeilingGrantExactlyWhatIsLeft()
	{
		const int maximum = 20;
		const int alreadySpent = 16;
		const int callers = 32;

		var project = await NewProjectAsync(backtests: maximum);

		IsTrue(await _store.TryClaimAsync(project, alreadySpent, 0, CancellationToken), "the opening claim was refused.");

		var granted = await RaceAsync(callers, () => _store.TryClaimAsync(project, 1, 0, CancellationToken).AsTask());

		AreEqual(maximum - alreadySpent, granted,
			$"{callers} callers competed for {maximum - alreadySpent} backtests and {granted} of them were granted one.");

		var budget = (await _store.OpenAsync(project, CancellationToken)).Budget;

		AreEqual(maximum, budget.ClaimedBacktests, "the project spent something other than exactly what it was granted.");
	}

	/// <summary>
	/// The same crowd at the candidate ceiling: exactly the candidates that are left are granted, and no
	/// backtest is spent along the way.
	/// </summary>
	[TestMethod]
	public async Task ClaimsRacingAtTheCandidateCeilingGrantExactlyWhatIsLeft()
	{
		const int maximum = 6;
		const int alreadySpent = 3;
		const int callers = 24;

		var project = await NewProjectAsync(backtests: 100, candidates: maximum);

		IsTrue(await _store.TryClaimAsync(project, 0, alreadySpent, CancellationToken), "the opening claim was refused.");

		var granted = await RaceAsync(callers, () => _store.TryClaimAsync(project, 0, 1, CancellationToken).AsTask());

		AreEqual(maximum - alreadySpent, granted,
			$"{callers} callers competed for {maximum - alreadySpent} candidates and {granted} of them were granted one.");

		var budget = (await _store.OpenAsync(project, CancellationToken)).Budget;

		AreEqual(maximum, budget.ClaimedCandidates, "the project created something other than exactly what it was granted.");
		AreEqual(0, budget.ClaimedBacktests, "a race over candidates spent backtests.");
	}

	/// <summary>A claim larger than what is left is refused whole rather than granted in part.</summary>
	[TestMethod]
	public async Task AClaimBeyondTheAllowanceIsRefusedWhole()
	{
		var project = await NewProjectAsync(backtests: 10);

		IsFalse(await _store.TryClaimAsync(project, 11, 0, CancellationToken), "a claim beyond the ceiling was granted.");

		var budget = ResearchBudget.Restore((await _store.OpenAsync(project, CancellationToken)).Budget);

		AreEqual(10, budget.RemainingBacktests, "a refused claim spent part of the allowance.");
	}

	/// <summary>Saving a project does not write back an allowance the caller read a moment ago.</summary>
	[TestMethod]
	public async Task SavingAProjectDoesNotUndoAClaim()
	{
		var project = await NewProjectAsync(backtests: 10);

		var stale = await _store.OpenAsync(project, CancellationToken);

		await _store.TryClaimAsync(project, 4, 0, CancellationToken);

		await _store.UpdateAsync(stale with { UpdatedAt = DateTime.UtcNow }, CancellationToken);

		var budget = ResearchBudget.Restore((await _store.OpenAsync(project, CancellationToken)).Budget);

		AreEqual(6, budget.RemainingBacktests, "saving the project handed the allowance back.");
	}

	/// <summary>
	/// A claim whose work never ran is given back, or a caller that walked away silently shrinks the
	/// allowance of the project it walked away from.
	/// </summary>
	[TestMethod]
	public async Task AnAbandonedClaimIsGivenBack()
	{
		var project = await NewProjectAsync(backtests: 10, candidates: 4);

		IsTrue(await _store.TryClaimAsync(project, 3, 2, CancellationToken), "the claim was refused.");

		await _store.ReleaseAsync(project, 3, 2, CancellationToken);

		var budget = (await _store.OpenAsync(project, CancellationToken)).Budget;

		AreEqual(0, budget.ClaimedBacktests, "an abandoned backtest claim was kept.");
		AreEqual(0, budget.ClaimedCandidates, "an abandoned candidate claim was kept.");
	}

	/// <summary>
	/// Giving back more than was ever claimed cannot leave the project with allowance nobody granted it.
	/// </summary>
	[TestMethod]
	public async Task ReleasingMoreThanWasClaimedInventsNoAllowance()
	{
		var project = await NewProjectAsync(backtests: 10, candidates: 4);

		IsTrue(await _store.TryClaimAsync(project, 1, 1, CancellationToken), "the claim was refused.");

		await _store.ReleaseAsync(project, 5, 5, CancellationToken);

		var budget = (await _store.OpenAsync(project, CancellationToken)).Budget;

		AreEqual(0, budget.ClaimedBacktests, "releasing more than was claimed took the count below nothing.");
		AreEqual(0, budget.ClaimedCandidates, "releasing more than was claimed took the count below nothing.");

		IsFalse(await _store.TryClaimAsync(project, 11, 0, CancellationToken),
			"a release stretched the ceiling past the ten backtests the project was granted.");
	}

	/// <summary>Time is spent by work that finished, and it runs out.</summary>
	[TestMethod]
	public async Task TimeIsChargedAndRunsOut()
	{
		var project = await NewProjectAsync(backtests: 100, wallClock: TimeSpan.FromMinutes(10));

		await _store.ChargeTimeAsync(project, TimeSpan.FromMinutes(4), CancellationToken);

		var half = ResearchBudget.Restore((await _store.OpenAsync(project, CancellationToken)).Budget);

		AreEqual(TimeSpan.FromMinutes(6), half.RemainingWallClock);
		IsTrue(await _store.TryClaimAsync(project, 1, 0, CancellationToken), "time was left and the claim was refused.");

		await _store.ChargeTimeAsync(project, TimeSpan.FromMinutes(6), CancellationToken);

		var spent = ResearchBudget.Restore((await _store.OpenAsync(project, CancellationToken)).Budget);

		AreEqual(TimeSpan.Zero, spent.RemainingWallClock);
		IsTrue(spent.IsExhausted, "a project with no machine time left says it has allowance.");

		IsFalse(await _store.TryClaimAsync(project, 1, 0, CancellationToken),
			"work was started on a project that has spent its machine time.");
	}

	/// <summary>
	/// Machine time is charged rather than claimed, and charges landing together are all of them charged.
	/// </summary>
	/// <remarks>
	/// The wall clock has no ceiling of its own to race for: it is added to as work finishes and it gates
	/// the claim once it is gone. What it shares with the claim is the arithmetic, and a charge that read
	/// the figure and wrote a sum back would lose every charge that landed while it was thinking - which
	/// is a project that runs for hours and reports minutes.
	/// </remarks>
	[TestMethod]
	public async Task TimeChargedTogetherIsAllCharged()
	{
		const int callers = 24;

		var project = await NewProjectAsync(backtests: 100, wallClock: TimeSpan.FromHours(2));

		var charged = await RaceAsync(callers, async () =>
		{
			await _store.ChargeTimeAsync(project, TimeSpan.FromMinutes(1), CancellationToken);

			return true;
		});

		AreEqual(callers, charged, "a charge did not complete.");

		var budget = (await _store.OpenAsync(project, CancellationToken)).Budget;

		AreEqual(TimeSpan.FromMinutes(callers), budget.ClaimedWallClock,
			$"{callers} minutes were charged a minute at a time and the project was billed for something else.");
	}

	/// <summary>
	/// Once the machine time is gone the crowd is turned away whole: the time gate is part of the same
	/// statement as the ceiling, so a claim cannot slip past it by arriving with others.
	/// </summary>
	[TestMethod]
	public async Task ClaimsRacingOnceTheTimeIsSpentAreAllRefused()
	{
		const int callers = 24;

		var project = await NewProjectAsync(backtests: 100, wallClock: TimeSpan.FromMinutes(10));

		await _store.ChargeTimeAsync(project, TimeSpan.FromMinutes(10), CancellationToken);

		var granted = await RaceAsync(callers, () => _store.TryClaimAsync(project, 1, 0, CancellationToken).AsTask());

		AreEqual(0, granted, "work was started on a project that has spent its machine time.");

		var budget = (await _store.OpenAsync(project, CancellationToken)).Budget;

		AreEqual(0, budget.ClaimedBacktests, "a refused claim spent a backtest.");
	}

	/// <summary>Candidates are counted apart from backtests.</summary>
	[TestMethod]
	public async Task CandidatesAreCountedApart()
	{
		var project = await NewProjectAsync(backtests: 10, candidates: 2);

		IsTrue(await _store.TryClaimAsync(project, 0, 1, CancellationToken));
		IsTrue(await _store.TryClaimAsync(project, 0, 1, CancellationToken));
		IsFalse(await _store.TryClaimAsync(project, 0, 1, CancellationToken), "a third candidate was granted out of two.");

		var budget = ResearchBudget.Restore((await _store.OpenAsync(project, CancellationToken)).Budget);

		AreEqual(10, budget.RemainingBacktests, "claiming candidates cost backtests.");
	}

	// Every caller waits on the same signal before it starts, so they are inside the claim together
	// rather than one after another. Claims issued in a loop mostly complete before the next one is
	// created - the work is a single statement against an open connection - and a test built that way
	// would say nothing about the case it was written for.
	private async Task<int> RaceAsync(int callers, Func<Task<bool>> attempt)
	{
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var granted = 0;

		var racing = Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
		{
			await start.Task;

			if (await attempt())
				Interlocked.Increment(ref granted);
		}, CancellationToken)).ToArray();

		start.SetResult();

		await Task.WhenAll(racing);

		return granted;
	}

	private async Task<ProjectId> NewProjectAsync(
		int backtests = 100,
		int candidates = 40,
		TimeSpan wallClock = default)
	{
		var project = ResearchProject.Create(
			"allowance",
			new ResearchBudget(
				backtests,
				candidates,
				wallClock == default ? TimeSpan.FromHours(1) : wallClock).ToState(),
			DateTime.UtcNow);

		await _store.CreateAsync(project, CancellationToken);

		return project.Id;
	}
}
