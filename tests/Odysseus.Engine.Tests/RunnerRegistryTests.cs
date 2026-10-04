namespace Odysseus.Engine.Tests;

using System.Collections.Concurrent;
using System.Linq;

/// <summary>
/// The directory a deployment survives as, and the one file two servers race for.
/// </summary>
/// <remarks>
/// A registry that lives in memory is a registry that dies with the process holding it, which is the
/// whole reason this one is a directory. What is worth pinning is the two things a directory makes
/// possible and a dictionary does not: a process that did not start a runner can read it, and two
/// processes that cannot see each other's memory can still settle which of them owns a project.
/// </remarks>
[TestClass]
public class RunnerRegistryTests : OdysseusTestBase
{
	private static readonly DateTime _booted = new(2026, 9, 1, 6, 12, 44, DateTimeKind.Utc);
	private static readonly DateTime _started = new(2026, 9, 3, 17, 31, 4, DateTimeKind.Utc);

	private string _root;

	/// <summary>Makes a projects root of its own.</summary>
	[TestInitialize]
	public void CreateRoot()
		=> _root = Path.Combine(Path.GetTempPath(), "odysseus-runners", Guid.NewGuid().ToString("n"));

	/// <summary>Removes it.</summary>
	[TestCleanup]
	public void DeleteRoot()
	{
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}

	/// <summary>
	/// A record written by one registry is read by another over the same root, which is what a later
	/// session in a different process amounts to.
	/// </summary>
	[TestMethod]
	public void ARunnerIsFoundByASessionThatDidNotStartIt()
	{
		var written = Record("dep_one", 24188);

		Registry().Home("dep_one").WriteRecord(written);

		// A second registry over the same directory, holding nothing of the first one's.
		var found = Registry().Read("dep_one");

		IsNotNull(found, "a runner recorded on disk was invisible to a session that did not start it.");
		AreEqual(written.ProcessId, found.ProcessId);
		AreEqual(written.Symbol, found.Symbol);
		AreEqual(TradingModes.Paper, found.Mode);
	}

	/// <summary>
	/// A runner that exited in an orderly fashion left no record and is not listed. Listing it would
	/// report a deployment that ended properly as something still to be dealt with.
	/// </summary>
	[TestMethod]
	public void ARunnerThatSaidGoodbyeIsNotListed()
	{
		var registry = Registry();
		var home = registry.Home("dep_one");

		home.WriteRecord(Record("dep_one", 24188));

		AreEqual(1, registry.Records().Count);

		home.DeleteRecord();

		AreEqual(0, registry.Records().Count, "a runner that removed its own record was still listed.");
	}

	/// <summary>
	/// A record torn in half by a process that died mid-write is read as no record, not as a failure.
	/// This session cannot say what is there, which is precisely what it goes on to report.
	/// </summary>
	[TestMethod]
	public void AHalfWrittenRecordIsNoRecord()
	{
		var registry = Registry();
		var home = registry.Home("dep_one");

		home.Create();

		File.WriteAllText(home.RecordFile, "{ \"schema\": 1, \"deploymentI");

		IsNull(registry.Read("dep_one"), "a half-written record was parsed into something.");
		AreEqual(0, registry.Records().Count);
	}

	/// <summary>
	/// The journal is what a session reads when the runner is gone, and it is read from the end: a line
	/// torn in half by the crash does not hide the whole one before it.
	/// </summary>
	[TestMethod]
	public void TheLastWholeLineOfTheJournalSurvivesTheCrashThatCutTheNextOne()
	{
		var home = Registry().Home("dep_one");

		home.Append(new(_started, RunnerPhases.Trading, TradingModes.Paper, 3m, 1, 2, 12.5m, "Still trading."));

		File.AppendAllText(home.JournalFile, "{\"at\":\"2026-09-03T17:4");

		var last = home.LastEntry();

		IsNotNull(last, "a torn last line hid the whole line before it.");
		AreEqual(3m, last.Position);
		AreEqual("Still trading.", last.What);
	}

	/// <summary>
	/// Two servers that cannot see each other's memory still settle which of them owns a project, and the
	/// loser is told rather than left to find out at the broker.
	/// </summary>
	/// <remarks>
	/// The rule this arbitrates - one deployment per project - cannot be enforced from a table both of
	/// them read before either writes: they both find nothing running and both start something, and the
	/// first anybody hears of it is two strategies trading against each other's positions on one account.
	/// </remarks>
	[TestMethod]
	public async Task TwoServersRacingForOneProjectProduceExactlyOneRunner()
	{
		const int racers = 8;

		var taken = new ConcurrentBag<string>();
		var refused = new ConcurrentBag<RunnerAlreadyRunningException>();
		var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var racing = Enumerable.Range(0, racers).Select(i => Task.Run(async () =>
		{
			// Each racer is its own registry over one root, which is what two servers on one machine are.
			var registry = Registry();

			await ready.Task;

			try
			{
				var home = registry.Claim("prj_one", $"dep_{i}");

				taken.Add(Path.GetFileName(home.Directory));

				// A runner that took the project and then announced itself, which is what makes the claim
				// stick against everybody who arrives afterwards.
				home.WriteRecord(Record($"dep_{i}", Environment.ProcessId));
			}
			catch (RunnerAlreadyRunningException refusal)
			{
				refused.Add(refusal);
			}
		}, CancellationToken)).ToArray();

		ready.SetResult();

		await Task.WhenAll(racing);

		AreEqual(1, taken.Count,
			$"{taken.Count} sessions started a runner on one project: {string.Join(", ", taken)}.");

		AreEqual(racers - 1, refused.Count, "somebody neither took the project nor was told why.");

		foreach (var refusal in refused)
			IsTrue(refusal.Message.Length > 0, "a refusal said nothing.");

		AreEqual(1, Registry().Records().Count, "more than one runner was recorded against one project.");
	}

	/// <summary>
	/// A claim left by a runner that has since died is taken over rather than honoured. The rule exists
	/// because of a live position, and a dead runner is not adding to one.
	/// </summary>
	[TestMethod]
	public void AClaimLeftByADeadRunnerIsTakenOver()
	{
		var registry = Registry();

		registry.Claim("prj_one", "dep_one").WriteRecord(Record("dep_one", 24188));

		// The machine restarted since, so every runner recorded before it is definitively gone.
		var later = new RunnerRegistry(_root, new Probe(_booted.AddDays(1)));

		var home = later.Claim("prj_one", "dep_two");

		AreEqual("dep_two", Path.GetFileName(home.Directory));
		AreEqual("dep_two", later.HolderOf("prj_one"));
	}

	/// <summary>
	/// Several sessions that all find a project held by a dead runner take it over exactly once between
	/// them. Clearing the dead claim must not clear the claim a faster racer has just put in its place.
	/// </summary>
	/// <remarks>
	/// Repeated over many projects because the window is narrow: a racer that read the dead holder before
	/// the winner moved its claim in, and deletes the name after, takes the project a second time.
	/// </remarks>
	[TestMethod]
	public async Task RacersTakingOverADeadClaimProduceExactlyOneRunner()
	{
		const int racers = 8;
		const int rounds = 50;

		for (var round = 0; round < rounds; round++)
		{
			var project = $"prj_{round}";

			// Process 0 is never alive on the probe, so this claim belongs to a runner that has died.
			Registry().Claim(project, $"dep_dead_{round}").WriteRecord(Record($"dep_dead_{round}", 0));

			var taken = new ConcurrentBag<string>();
			var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

			var racing = Enumerable.Range(0, racers).Select(i => Task.Run(async () =>
			{
				var registry = Registry();

				await ready.Task;

				try
				{
					var home = registry.Claim(project, $"dep_{round}_{i}");

					taken.Add(Path.GetFileName(home.Directory));

					home.WriteRecord(Record($"dep_{round}_{i}", Environment.ProcessId));
				}
				catch (RunnerAlreadyRunningException)
				{
				}
			}, CancellationToken)).ToArray();

			ready.SetResult();

			await Task.WhenAll(racing);

			AreEqual(1, taken.Count,
				$"round {round}: {taken.Count} sessions took over one dead claim: {string.Join(", ", taken)}.");
		}
	}

	/// <summary>A claim held by a live runner is refused, and the refusal names who has it.</summary>
	[TestMethod]
	public void AClaimHeldByALiveRunnerIsRefused()
	{
		var registry = Registry();

		registry.Claim("prj_one", "dep_one").WriteRecord(Record("dep_one", 24188));

		var refusal = Throws<RunnerAlreadyRunningException>(() => registry.Claim("prj_one", "dep_two"));

		IsTrue(refusal.Message.Contains("dep_one", StringComparison.Ordinal),
			$"the refusal does not say which deployment has the project: {refusal.Message}");
	}

	/// <summary>
	/// A claim that cannot be read is left where it is, and the project it holds is not taken.
	/// </summary>
	/// <remarks>
	/// This is the race in <see cref="TwoServersRacingForOneProjectProduceExactlyOneRunner"/> written down
	/// as the one decision it turns on, because a race reproduces when it feels like it and a decision can
	/// be stated. A claim is written whole under a name of its own and then moved onto this one, so a
	/// claim that is there is a claim that parses: a name occupied by something unreadable is a name
	/// somebody is moving their claim onto as this reads it. Deleting it and taking the project asks
	/// nobody whether the holder is alive - the record beside it says a runner is trading - and the first
	/// anybody hears of that is two strategies on one account.
	///
	/// The file is torn here rather than left absent, which is the difference that matters: an empty name
	/// is competed for again, and an occupied one is never cleared by whoever failed to read it.
	/// </remarks>
	[TestMethod]
	public void AClaimThatCannotBeReadIsNeitherTakenOverNorDeleted()
	{
		var registry = Registry();

		registry.Claim("prj_one", "dep_one").WriteRecord(Record("dep_one", 24188));

		var claim = Directory.EnumerateFiles(Path.Combine(registry.Directory, "claims")).Single();

		File.WriteAllText(claim, "{\"deploymentId\":\"dep_o");

		var refusal = Throws<RunnerAlreadyRunningException>(() => registry.Claim("prj_one", "dep_two"));

		IsTrue(refusal.Message.Length > 0, "a refusal said nothing.");

		IsTrue(File.Exists(claim),
			"a claim that could not be read was deleted, and the project a live runner was holding went with it.");
	}

	/// <summary>
	/// Releasing gives the project back only when the deployment releasing it is the one holding it, so a
	/// stale stop cannot clear the way for a second runner while the first is still trading.
	/// </summary>
	[TestMethod]
	public void OnlyTheHolderCanReleaseAProject()
	{
		var registry = Registry();

		registry.Claim("prj_one", "dep_one").WriteRecord(Record("dep_one", 24188));

		registry.Release("prj_one", "dep_two");

		AreEqual("dep_one", registry.HolderOf("prj_one"), "somebody else's claim was released.");

		registry.Release("prj_one", "dep_one");

		IsNull(registry.HolderOf("prj_one"));
	}

	/// <summary>
	/// A claim asked for twice by the same deployment is the same claim, so a retry after a dropped
	/// connection does not refuse the caller its own project.
	/// </summary>
	[TestMethod]
	public void AskingTwiceForOnesOwnClaimIsNotARefusal()
	{
		var registry = Registry();

		var first = registry.Claim("prj_one", "dep_one");
		var again = registry.Claim("prj_one", "dep_one");

		AreEqual(first.Directory, again.Directory);
	}

	private static RunnerRecord Record(string deploymentId, int processId)
		=> new(
			RunnerHome.Schema,
			RunnerProtocol.Version,
			"stub-engine",
			deploymentId,
			"prj_one",
			"cnd_one",
			TradingModes.Paper,
			"AAPL",
			10m,
			"StockSharp.Stub",
			"1.0.0",
			"stub-account",
			RunnerProtocol.PipeOf(deploymentId),
			processId,
			_started,
			_booted,
			_started);

	private RunnerRegistry Registry() => new(_root, new Probe(_booted));

	/// <summary>
	/// A machine on which every recorded process is still there, so what these exercise is the claim
	/// rather than the liveness rules next door.
	/// </summary>
	private sealed class Probe(DateTime booted) : IProcessProbe
	{
		public DateTime BootedAt => booted;

		public DateTime? StartedAt(int processId) => processId > 0 ? _started : null;
	}
}
