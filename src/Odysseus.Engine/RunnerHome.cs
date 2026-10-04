namespace Odysseus.Engine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

using Odysseus.Application;
using Odysseus.Domain;

/// <summary>
/// What a runner is to run, and everything it needs in order to reach a broker by itself.
/// </summary>
/// <param name="Schema">Version of this file.</param>
/// <param name="DeploymentId">Deployment the runner is running.</param>
/// <param name="ProjectId">Project the deployment belongs to.</param>
/// <param name="CandidateId">Candidate being run.</param>
/// <param name="ClassName">Name of the strategy inside the assembly.</param>
/// <param name="Assembly">File name of the assembly, beside this one in the runner's home.</param>
/// <param name="Symbol">Symbol to trade.</param>
/// <param name="TimeFrame">Length of one candle, which the rules are evaluated on.</param>
/// <param name="Volume">Size of one position.</param>
/// <param name="Parameters">Values the run was measured with.</param>
/// <param name="Connector">Which connector to load, and how to configure it.</param>
/// <param name="ConnectorCache">Directory downloaded connectors are kept in.</param>
/// <param name="ConnectorSources">Where a connector package may be downloaded from.</param>
/// <param name="ConnectorAllow">Package identifier prefixes the runner may load; an empty list allows none.</param>
/// <param name="Pipe">Name of the pipe the runner is to listen on.</param>
/// <param name="Token">What a client must present before the runner will answer it.</param>
/// <param name="Heartbeat">How often the runner writes what it is holding into its journal.</param>
/// <remarks>
/// Everything the runner needs is here, because the runner never opens the project database: that store
/// keeps one connection behind a semaphore with no write-ahead log and no busy timeout, and its own
/// documentation says two writers on one file is a locking problem invented for the sake of a smaller
/// class. The server stays the only writer, and the runner's authority lives in its own directory.
///
/// Credentials are deliberately absent. They arrive as a path in the child's environment, as the
/// credential file's own documentation insists, which is the exact inverse of the worker: a worker has
/// those variables removed because it must never reach an account, and a runner is given them because
/// reaching an account is the whole of its job. Settings are safe to write down here because a settings
/// dictionary cannot carry a secret - the configurator refuses every credential name and the demo flag
/// as setting names, before anything is downloaded.
/// </remarks>
public sealed record RunnerPlan(
	int Schema,
	string DeploymentId,
	string ProjectId,
	string CandidateId,
	string ClassName,
	string Assembly,
	string Symbol,
	TimeSpan TimeFrame,
	decimal Volume,
	IReadOnlyDictionary<string, decimal> Parameters,
	ConnectorChoice Connector,
	string ConnectorCache,
	IReadOnlyList<string> ConnectorSources,
	IReadOnlyList<string> ConnectorAllow,
	string Pipe,
	string Token,
	TimeSpan Heartbeat);

/// <summary>
/// How to find a runner and how to tell it apart from whatever now holds its process number.
/// </summary>
/// <param name="Schema">Version of this file.</param>
/// <param name="Protocol">Version of the protocol the runner speaks.</param>
/// <param name="Engine">Which build of the trading platform it carries.</param>
/// <param name="DeploymentId">Deployment it is running.</param>
/// <param name="ProjectId">Project the deployment belongs to.</param>
/// <param name="CandidateId">Candidate it is running.</param>
/// <param name="Mode">Which account it is on, so a session that cannot reach it still reports the mode.</param>
/// <param name="Symbol">Symbol it trades.</param>
/// <param name="Volume">Size of one position.</param>
/// <param name="PackageId">Connector package it loaded.</param>
/// <param name="PackageVersion">Version of that package.</param>
/// <param name="Account">The account the broker reported, or empty before it had connected.</param>
/// <param name="Pipe">Pipe it listens on.</param>
/// <param name="ProcessId">Its process.</param>
/// <param name="ProcessStartedAt">When that process started, in UTC.</param>
/// <param name="BootedAt">When the machine it is on last started, in UTC.</param>
/// <param name="StartedAt">When the runner started, in UTC.</param>
/// <remarks>
/// Written before the runner connects to anything, so a launcher that dies between the spawn and the
/// handshake leaves a discoverable process rather than an orphan. Deleted by the runner on an orderly
/// exit; left behind by a crash, which is what makes a crash discoverable.
///
/// The process number alone is not identity. Numbers are reused, and a stale record plus a recycled one
/// names an unrelated process, which a tool would then report as a running strategy. The two moments
/// fix different halves of that, and <see cref="RunnerLiveness"/> is where they are applied.
/// </remarks>
public sealed record RunnerRecord(
	int Schema,
	int Protocol,
	string Engine,
	string DeploymentId,
	string ProjectId,
	string CandidateId,
	TradingModes Mode,
	string Symbol,
	decimal Volume,
	string PackageId,
	string PackageVersion,
	string Account,
	string Pipe,
	int ProcessId,
	DateTime ProcessStartedAt,
	DateTime BootedAt,
	DateTime StartedAt);

/// <summary>
/// One line of what a runner did, written as it happened.
/// </summary>
/// <param name="At">When it happened, in UTC.</param>
/// <param name="Phase">Where the runner had got to.</param>
/// <param name="Mode">Which account it was on.</param>
/// <param name="Position">What it was holding.</param>
/// <param name="WorkingOrders">Orders still live at the venue.</param>
/// <param name="Trades">Positions it had opened and closed.</param>
/// <param name="RealizedProfit">What those had come to.</param>
/// <param name="What">What happened, in words.</param>
/// <remarks>
/// The journal is what a session has to read when the runner is gone. Without it the last thing anybody
/// knows about a dead runner is whatever the last observation happened to catch, which is usually the
/// moment before the interesting one.
/// </remarks>
public sealed record RunnerJournalEntry(
	DateTime At,
	RunnerPhases Phase,
	TradingModes Mode,
	decimal Position,
	int WorkingOrders,
	int Trades,
	decimal RealizedProfit,
	string What);

/// <summary>
/// The directory one runner owns, and the files in it.
/// </summary>
/// <remarks>
/// A deployment survives as a directory. The record says how to reach the process, the journal says what
/// it did, the assembly is the exact bytes being traded, and the plan is everything it was told - so a
/// session that never started it, in a process that did not exist when it did, can read all four.
/// </remarks>
public sealed class RunnerHome
{
	/// <summary>Version of the two documents this build writes.</summary>
	public const int Schema = 1;

	/// <summary>Folder under the projects root that every runner's home lives in.</summary>
	public const string RunnersFolder = "runners";

	/// <summary>How the two documents are written: indented, because a person reads them.</summary>
	private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.General)
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = true,
	};

	/// <summary>How a journal line is written: one line, because the file is read a line at a time.</summary>
	private static readonly JsonSerializerOptions _line = new(JsonSerializerDefaults.General)
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		WriteIndented = false,
	};

	/// <summary>
	/// Names a home.
	/// </summary>
	/// <param name="directory">The directory, which need not exist yet.</param>
	/// <param name="deploymentId">Deployment the directory belongs to.</param>
	public RunnerHome(string directory, string deploymentId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

		Directory = Path.GetFullPath(directory);
		DeploymentId = deploymentId;
	}

	/// <summary>The directory itself.</summary>
	public string Directory { get; }

	/// <summary>Deployment this home belongs to.</summary>
	public string DeploymentId { get; }

	/// <summary>What the runner was told to run.</summary>
	public string PlanFile => Path.Combine(Directory, "launch.json");

	/// <summary>How to find the runner and tell it apart from a reused process number.</summary>
	public string RecordFile => Path.Combine(Directory, "runner.json");

	/// <summary>The exact assembly bytes being traded.</summary>
	public string AssemblyFile => Path.Combine(Directory, "strategy.dll");

	/// <summary>What the runner did, one line at a time.</summary>
	public string JournalFile => Path.Combine(Directory, "journal.jsonl");

	/// <summary>What the runner has to say to a person.</summary>
	public string LogFile => Path.Combine(Directory, "runner.log");

	/// <summary>Makes the directory, if it is not there.</summary>
	public void Create() => System.IO.Directory.CreateDirectory(Directory);

	/// <summary>
	/// Writes what the runner is to run.
	/// </summary>
	/// <param name="plan">The plan.</param>
	public void WritePlan(RunnerPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);

		Create();
		Replace(PlanFile, JsonSerializer.SerializeToUtf8Bytes(plan, _json));
	}

	/// <summary>
	/// Reads what the runner was told to run.
	/// </summary>
	/// <returns>The plan.</returns>
	/// <exception cref="FileNotFoundException">There is no plan in this home.</exception>
	public RunnerPlan ReadPlan()
	{
		if (!File.Exists(PlanFile))
			throw new FileNotFoundException($"There is no launch plan at '{PlanFile}'.", PlanFile);

		return JsonSerializer.Deserialize<RunnerPlan>(File.ReadAllBytes(PlanFile), _json);
	}

	/// <summary>
	/// Writes how to find this runner.
	/// </summary>
	/// <param name="record">The record.</param>
	public void WriteRecord(RunnerRecord record)
	{
		ArgumentNullException.ThrowIfNull(record);

		Create();
		Replace(RecordFile, JsonSerializer.SerializeToUtf8Bytes(record, _json));
	}

	/// <summary>
	/// Reads how to find this runner.
	/// </summary>
	/// <returns>The record, or <see langword="null"/> when there is none or it cannot be read.</returns>
	/// <remarks>
	/// A record that will not parse is reported as no record rather than as a failure. It was written by
	/// a process that may no longer exist and read by one that did not write it, and a half-written file
	/// is one of the ordinary outcomes of that - it means this session cannot say what is there, which is
	/// exactly what <see cref="RunnerStatuses.Unknown"/> says.
	/// </remarks>
	public RunnerRecord ReadRecord()
	{
		try
		{
			if (!File.Exists(RecordFile))
				return null;

			return JsonSerializer.Deserialize<RunnerRecord>(File.ReadAllBytes(RecordFile), _json);
		}
		catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// Removes the record, which is what an orderly exit does.
	/// </summary>
	public void DeleteRecord()
	{
		try
		{
			File.Delete(RecordFile);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			// A record that could not be deleted is read back as a process that is gone, because the
			// liveness rules answer from the process and not from the file. Nothing is lost by leaving it.
		}
	}

	/// <summary>
	/// Adds one line to the journal.
	/// </summary>
	/// <param name="entry">What happened.</param>
	/// <remarks>
	/// Written as one append, through a handle that does not lock the file against a second writer.
	/// Several things inside a runner write a line at the moment it happens - the heartbeat, a stop
	/// arriving on the pipe, an interrupt at the terminal - and two of them do land together: an
	/// interrupt is precisely the case where the handler and the work it interrupted both have something
	/// to say. A handle that excluded the other one would fail exactly there, in the middle of recording
	/// why a runner ended, and appending in one call is what keeps two lines from tearing each other in
	/// half.
	/// </remarks>
	public void Append(RunnerJournalEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);

		Create();

		var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, _line) + Environment.NewLine);

		using var journal = new FileStream(
			JournalFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

		journal.Write(line);
	}

	/// <summary>
	/// The last thing the runner wrote down.
	/// </summary>
	/// <returns>The entry, or <see langword="null"/> when there is no journal or nothing readable in it.</returns>
	/// <remarks>
	/// Read backwards through the file's own lines rather than by remembering anything, because the
	/// caller of this is usually a process that was not running when any of them were written.
	/// </remarks>
	public RunnerJournalEntry LastEntry()
	{
		try
		{
			if (!File.Exists(JournalFile))
				return null;

			var lines = File.ReadAllLines(JournalFile);

			for (var i = lines.Length - 1; i >= 0; i--)
			{
				if (string.IsNullOrWhiteSpace(lines[i]))
					continue;

				try
				{
					return JsonSerializer.Deserialize<RunnerJournalEntry>(lines[i], _line);
				}
				catch (JsonException)
				{
					// A line torn in half by a process that died mid-write. The one before it is whole.
					continue;
				}
			}

			return null;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// Writes a file whole or not at all, so a reader never sees half of one.
	/// </summary>
	/// <param name="path">Where to write it.</param>
	/// <param name="content">What to write.</param>
	private static void Replace(string path, byte[] content)
	{
		var temporary = path + ".writing";

		File.WriteAllBytes(temporary, content);
		File.Move(temporary, path, overwrite: true);
	}
}
