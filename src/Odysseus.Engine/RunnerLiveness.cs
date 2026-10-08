namespace StockSharp.Odysseus.Engine;

using System.ComponentModel;
using System.Diagnostics;

/// <summary>
/// Whether the process a record names is still the process that wrote it.
/// </summary>
public enum RunnerProcesses
{
	/// <summary>The process is there and it is the one the record was written by.</summary>
	Alive,

	/// <summary>The process is not there, or what holds its number now is somebody else.</summary>
	Gone,
}

/// <summary>
/// What the operating system says about a process number, and when the machine last started.
/// </summary>
/// <remarks>
/// An interface because both answers are the machine's, and a test that had to arrange a real process
/// with a chosen start time - or reboot the machine - could not be written at all. The two failures this
/// exists to catch are exactly the two that never happen while anybody is watching.
/// </remarks>
public interface IProcessProbe
{
	/// <summary>When the machine last started, in UTC.</summary>
	DateTime BootedAt { get; }

	/// <summary>
	/// When the process with a number started.
	/// </summary>
	/// <param name="processId">The process number.</param>
	/// <returns>The moment, in UTC, or <see langword="null"/> when there is no such process.</returns>
	DateTime? StartedAt(int processId);
}

/// <summary>
/// The machine's own answers.
/// </summary>
public sealed class SystemProcessProbe : IProcessProbe
{
	/// <summary>The one every host uses.</summary>
	public static SystemProcessProbe Instance { get; } = new();

	/// <inheritdoc />
	/// <remarks>
	/// Derived from how long the machine has been up rather than read from a clock somebody may have
	/// changed, and rounded to the second so that two readings taken minutes apart compare equal.
	/// </remarks>
	public DateTime BootedAt
	{
		get
		{
			var booted = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

			return new(booted.Ticks - (booted.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
		}
	}

	/// <inheritdoc />
	public DateTime? StartedAt(int processId)
	{
		if (processId <= 0)
			return null;

		try
		{
			using var process = Process.GetProcessById(processId);

			return process.StartTime.ToUniversalTime();
		}
		catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception)
		{
			// No such process, one that exited between the lookup and the read, or one this account may
			// not look at. None of the three is a runner this session can reach, and the answer to all
			// three is the same: treat it as gone rather than as a strategy that is still trading.
			return null;
		}
	}
}

/// <summary>
/// Whether a runner recorded some time ago is still there.
/// </summary>
/// <remarks>
/// The question a process number cannot answer on its own. Numbers are reused, so a record left by a
/// crash plus a recycled number names an unrelated process - and a tool that took that for a match would
/// report a strategy as trading when nothing is, which is the one report nobody re-reads.
///
/// Two facts fix two different halves of it. The moment the process started tells one process from
/// another that happens to hold its number. The moment the machine started makes a whole class of
/// records definitively dead with no lookup at all: every runner recorded before the last restart is
/// gone, whatever now answers to its number.
/// </remarks>
public static class RunnerLiveness
{
	/// <summary>
	/// How far apart two readings of the boot moment may be before they are taken for different boots.
	/// </summary>
	/// <remarks>
	/// The moment is derived from a tick count that drifts against the wall clock and is not adjusted for
	/// time spent asleep, so two honest readings on one uptime differ by a little. A minute is wide enough
	/// for that drift and far narrower than any restart, and being wrong in the wide direction only costs
	/// a process lookup - the one that follows would still have to agree.
	/// </remarks>
	public static TimeSpan BootTolerance { get; } = TimeSpan.FromMinutes(1);

	/// <summary>
	/// How far apart two readings of a process's start may be before they are taken for different processes.
	/// </summary>
	/// <remarks>
	/// Nominally zero: the moment comes from the kernel and does not move. A second of slack absorbs the
	/// rounding a serialized moment can pick up, and to be wrong it would need a number reused inside one
	/// second by a process that also started within one second of the original - at which point the boot
	/// rule above is what is doing the work anyway.
	/// </remarks>
	public static TimeSpan StartTolerance { get; } = TimeSpan.FromSeconds(1);

	/// <summary>
	/// Whether the runner a record names is still running.
	/// </summary>
	/// <param name="record">What was written down when it started.</param>
	/// <param name="probe">What the machine says now.</param>
	/// <returns>Whether it is there.</returns>
	public static RunnerProcesses Of(RunnerRecord record, IProcessProbe probe)
	{
		ArgumentNullException.ThrowIfNull(record);

		return Of(record.ProcessId, record.ProcessStartedAt, record.BootedAt, probe);
	}

	/// <summary>
	/// Whether the process something was written down about is still that process.
	/// </summary>
	/// <param name="processId">The process number that was written down.</param>
	/// <param name="processStartedAt">When that process had started, in UTC.</param>
	/// <param name="bootedAt">When the machine had last started, in UTC.</param>
	/// <param name="probe">What the machine says now.</param>
	/// <returns>Whether it is there.</returns>
	/// <remarks>
	/// Taken apart from the record because a runner's record is not the only thing that names a process
	/// this way: a claim on a project names the session that made it, and it has to be possible to tell a
	/// session that is starting a runner right now from one that died halfway through.
	/// </remarks>
	public static RunnerProcesses Of(int processId, DateTime processStartedAt, DateTime bootedAt, IProcessProbe probe)
	{
		ArgumentNullException.ThrowIfNull(probe);

		// The machine has restarted, so everything recorded before it is gone - with no process lookup and
		// therefore no chance of matching a number that has since been handed to somebody else.
		if (Apart(probe.BootedAt, bootedAt) > BootTolerance)
			return RunnerProcesses.Gone;

		var started = probe.StartedAt(processId);

		if (started is null)
			return RunnerProcesses.Gone;

		// Something holds the number. Whether it is ours is a different question, and this is the one that
		// answers it: a process that started at another moment is another process.
		return Apart(started.Value, processStartedAt) <= StartTolerance
			? RunnerProcesses.Alive
			: RunnerProcesses.Gone;
	}

	/// <summary>
	/// Why a record was read the way it was, in words a person can act on.
	/// </summary>
	/// <param name="record">What was written down when it started.</param>
	/// <param name="probe">What the machine says now.</param>
	/// <returns>The explanation.</returns>
	public static string Explain(RunnerRecord record, IProcessProbe probe)
	{
		ArgumentNullException.ThrowIfNull(record);
		ArgumentNullException.ThrowIfNull(probe);

		if (Apart(probe.BootedAt, record.BootedAt) > BootTolerance)
		{
			return
				$"The machine has restarted since this runner was recorded, so process {record.ProcessId} is " +
				"not it. Nothing was stopped in an orderly fashion: whatever it held at the broker was left " +
				"exactly as it stood, and whether its working orders survived is the venue's rule and not " +
				"this product's.";
		}

		var started = probe.StartedAt(record.ProcessId);

		if (started is null)
			return $"There is no process {record.ProcessId}. The runner is gone and it left whatever it held as it stood.";

		if (Apart(started.Value, record.ProcessStartedAt) > StartTolerance)
		{
			return
				$"Process {record.ProcessId} exists but started at {started.Value:O}, and the runner recorded " +
				$"{record.ProcessStartedAt:O}. The number was reused by something else, so the runner is gone.";
		}

		return $"Process {record.ProcessId} is the one that was recorded.";
	}

	private static TimeSpan Apart(DateTime first, DateTime second)
	{
		var difference = first - second;

		return difference < TimeSpan.Zero ? -difference : difference;
	}
}
