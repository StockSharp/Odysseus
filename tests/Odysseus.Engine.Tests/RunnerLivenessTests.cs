namespace Odysseus.Engine.Tests;

/// <summary>
/// Telling a runner from whatever now holds its process number.
/// </summary>
/// <remarks>
/// The one question a record cannot answer on its own, and the one whose wrong answer is expensive in a
/// particular direction: reporting a dead runner as alive tells a person that something is watching a
/// position when nothing is, and it is the report nobody re-reads.
///
/// Both failures happen only when nobody is watching - a machine restarts overnight, a number is handed
/// to something else hours later - so they are arranged here rather than waited for.
/// </remarks>
[TestClass]
public class RunnerLivenessTests : OdysseusTestBase
{
	private static readonly DateTime _booted = new(2026, 9, 1, 6, 12, 44, DateTimeKind.Utc);
	private static readonly DateTime _started = new(2026, 9, 3, 17, 31, 4, DateTimeKind.Utc);

	/// <summary>A machine that says the same thing it said, with the process still there, is the same runner.</summary>
	[TestMethod]
	public void ARunnerWhoseProcessIsStillThereIsAlive()
	{
		var probe = new Probe(_booted, new() { [24188] = _started });

		AreEqual(RunnerProcesses.Alive, RunnerLiveness.Of(Record(24188), probe));
	}

	/// <summary>No such process is no runner, whatever the record says.</summary>
	[TestMethod]
	public void ARunnerWithNoProcessIsGone()
	{
		var probe = new Probe(_booted, new());

		AreEqual(RunnerProcesses.Gone, RunnerLiveness.Of(Record(24188), probe));

		IsTrue(RunnerLiveness.Explain(Record(24188), probe).Contains("24188", StringComparison.Ordinal),
			"the explanation does not name the process a person would go and look for.");
	}

	/// <summary>
	/// A process number handed to something else is not the runner. This is the failure the number alone
	/// cannot catch, and reporting it as alive would say a strategy is trading when the thing holding its
	/// number is a text editor.
	/// </summary>
	[TestMethod]
	public void AReusedProcessNumberIsNotTheRunner()
	{
		// Same number, started three hours after the runner recorded it: the number was recycled.
		var probe = new Probe(_booted, new() { [24188] = _started.AddHours(3) });

		AreEqual(RunnerProcesses.Gone, RunnerLiveness.Of(Record(24188), probe),
			"a recycled process number was read as the runner that used to hold it.");

		var why = RunnerLiveness.Explain(Record(24188), probe);

		IsTrue(why.Contains("reused", StringComparison.OrdinalIgnoreCase),
			$"the explanation does not say the number was reused: {why}");
	}

	/// <summary>
	/// After a restart every runner recorded before it is gone, and it is answered without a process
	/// lookup at all - so a number the new boot happens to have handed out cannot produce a false match.
	/// </summary>
	[TestMethod]
	public void EveryRunnerRecordedBeforeARestartIsGone()
	{
		// The machine has been up for an hour; the record was written before it went down, and something
		// on this boot holds the same number and even claims the same start time.
		var probe = new Probe(_booted.AddDays(2), new() { [24188] = _started });

		AreEqual(RunnerProcesses.Gone, RunnerLiveness.Of(Record(24188), probe),
			"a record from before the last restart was read as a running strategy.");

		IsTrue(RunnerLiveness.Explain(Record(24188), probe).Contains("restarted", StringComparison.OrdinalIgnoreCase),
			"the explanation does not say the machine restarted.");
	}

	/// <summary>
	/// The boot moment is derived from a tick count that drifts, so two honest readings on one uptime
	/// differ by a little. A little is not a restart.
	/// </summary>
	[TestMethod]
	public void DriftInTheBootMomentIsNotARestart()
	{
		var probe = new Probe(_booted.AddSeconds(20), new() { [24188] = _started });

		AreEqual(RunnerProcesses.Alive, RunnerLiveness.Of(Record(24188), probe),
			"twenty seconds of drift in a derived boot time was read as the machine having restarted.");
	}

	/// <summary>A process number of zero is nothing to look for, and is not a match with anything.</summary>
	[TestMethod]
	public void ARecordWithNoProcessIsGone()
	{
		AreEqual(RunnerProcesses.Gone, RunnerLiveness.Of(Record(0), new Probe(_booted, new())));
	}

	private static RunnerRecord Record(int processId)
		=> new(
			RunnerHome.Schema,
			RunnerProtocol.Version,
			"stub-engine",
			"dep_abcdef",
			"prj_abcdef",
			"cnd_abcdef",
			TradingModes.Paper,
			"AAPL",
			10m,
			"StockSharp.Stub",
			"1.0.0",
			"stub-account",
			"odysseus-runner-dep_abcdef",
			processId,
			_started,
			_booted,
			_started);

	/// <summary>A machine whose answers are arranged rather than waited for.</summary>
	private sealed class Probe(DateTime booted, Dictionary<int, DateTime> processes) : IProcessProbe
	{
		public DateTime BootedAt => booted;

		public DateTime? StartedAt(int processId)
			=> processes.TryGetValue(processId, out var started) ? started : null;
	}
}
