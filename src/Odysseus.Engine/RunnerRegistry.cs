namespace StockSharp.Odysseus.Engine;

using System.Linq;
using System.Text;
using System.Text.Json;

/// <summary>
/// Who holds a project, and which session said so.
/// </summary>
/// <param name="DeploymentId">Deployment the project was taken for.</param>
/// <param name="ProcessId">The session that took it.</param>
/// <param name="ProcessStartedAt">When that session started, in UTC.</param>
/// <param name="BootedAt">When the machine it is on last started, in UTC.</param>
/// <remarks>
/// The session is named as well as the deployment because of the moment between the two: a claim is
/// taken before the runner exists, so for a second or two there is a claim with no runner behind it. A
/// claim that named only the deployment would look stale in exactly that moment, and a second session
/// arriving there would take the project out from under the first one.
/// </remarks>
public sealed record RunnerClaim(
	string DeploymentId,
	int ProcessId,
	DateTime ProcessStartedAt,
	DateTime BootedAt);

/// <summary>
/// Every runner on this machine that a projects root knows about.
/// </summary>
/// <remarks>
/// The registry is a directory, which is the whole point: it survives the process that wrote it, it can
/// be read by a process that did not, and it can be looked at by a person with a file browser while both
/// are gone. Nothing here holds state between calls - every answer is read off the disk when it is
/// asked for, because the alternative is a cache that is wrong exactly when it matters.
/// </remarks>
public sealed class RunnerRegistry
{
	private const string ClaimsFolder = "claims";
	private const string ClaimExtension = ".claim";
	private const string LockExtension = ".lock";

	private static readonly JsonSerializerOptions _claims = new(JsonSerializerDefaults.General)
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	private readonly IProcessProbe _probe;

	/// <summary>
	/// Opens the registry under a projects root.
	/// </summary>
	/// <param name="projectsRoot">Directory the projects live in.</param>
	/// <param name="probe">What the machine says about a process number.</param>
	public RunnerRegistry(string projectsRoot, IProcessProbe probe)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectsRoot);

		_probe = probe ?? throw new ArgumentNullException(nameof(probe));

		Directory = Path.Combine(Path.GetFullPath(projectsRoot), RunnerHome.RunnersFolder);
	}

	/// <summary>Where the runners' homes are.</summary>
	public string Directory { get; }

	/// <summary>
	/// The home of one deployment, whether or not anything has been written into it yet.
	/// </summary>
	/// <param name="deploymentId">Deployment to name.</param>
	/// <returns>The home.</returns>
	public RunnerHome Home(string deploymentId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

		return new(Path.Combine(Directory, Safe(deploymentId)), deploymentId);
	}

	/// <summary>
	/// What a deployment's runner wrote about itself.
	/// </summary>
	/// <param name="deploymentId">Deployment to look for.</param>
	/// <returns>The record, or <see langword="null"/> when there is none.</returns>
	public RunnerRecord Read(string deploymentId) => Home(deploymentId).ReadRecord();

	/// <summary>
	/// Every runner that left a record, whether or not this session started it.
	/// </summary>
	/// <returns>The records, oldest first.</returns>
	/// <remarks>
	/// A home with no record is a runner that exited in an orderly fashion, and it is not listed: what
	/// survives it is the row in the project database and the journal in its home, both of which are read
	/// by name. Listing it here would report a stopped deployment as something still to be dealt with.
	/// </remarks>
	public IReadOnlyList<RunnerRecord> Records()
	{
		if (!System.IO.Directory.Exists(Directory))
			return [];

		var found = new List<RunnerRecord>();

		foreach (var directory in System.IO.Directory.EnumerateDirectories(Directory))
		{
			var name = Path.GetFileName(directory);

			if (string.Equals(name, ClaimsFolder, StringComparison.Ordinal))
				continue;

			if (new RunnerHome(directory, name).ReadRecord() is { } record)
				found.Add(record);
		}

		return [.. found.OrderBy(r => r.StartedAt)];
	}

	/// <summary>
	/// Whether the process a record names is still the one that wrote it.
	/// </summary>
	/// <param name="record">The record.</param>
	/// <returns>Whether it is there.</returns>
	public RunnerProcesses Presence(RunnerRecord record) => RunnerLiveness.Of(record, _probe);

	/// <summary>
	/// Why a record reads the way it does, in words a person can act on.
	/// </summary>
	/// <param name="record">The record.</param>
	/// <returns>The explanation.</returns>
	public string Explain(RunnerRecord record) => RunnerLiveness.Explain(record, _probe);

	/// <summary>
	/// Takes the project on behalf of a deployment, or refuses because somebody else has it.
	/// </summary>
	/// <param name="projectId">Project being taken.</param>
	/// <param name="deploymentId">Deployment taking it.</param>
	/// <returns>The runner's home, made and empty.</returns>
	/// <exception cref="RunnerAlreadyRunningException">A live runner already has this project.</exception>
	/// <remarks>
	/// The one-running-deployment rule, arbitrated where two processes can both see it. A row in the
	/// project database cannot do it: two servers over one root read the table, both find nothing
	/// running, and both write a row - and the first anybody hears of the collision is two strategies
	/// trading against each other's positions on one account.
	///
	/// A claim is a file, put on the project's name while holding the lock every taker and every remover
	/// of a claim holds, so exactly one of two racing callers gets it. A claim left behind by a runner
	/// that has since died, or by a session that died before it started one, is taken over rather than
	/// honoured - the whole reason for the rule is a live position, and neither of those is adding to one.
	/// </remarks>
	public RunnerHome Claim(string projectId, string deploymentId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
		ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

		var path = ClaimFile(projectId);

		System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));

		var mine = new RunnerClaim(
			deploymentId,
			Environment.ProcessId,
			_probe.StartedAt(Environment.ProcessId) ?? DateTime.UtcNow,
			_probe.BootedAt);

		// Twice: the first attempt may lose to a claim that turns out to be stale, and the second is
		// decided under the same lock - so a third caller arriving in between still loses.
		for (var attempt = 0; attempt < 2; attempt++)
		{
			if (Took(path, mine))
			{
				var taken = Home(deploymentId);

				taken.Create();

				return taken;
			}

			// Somebody has it. Whether they still exist is the next question.

			var holder = Holder(path);

			if (holder is null)
			{
				// Nothing readable is on the name, and that is never a reason to take the project. A
				// claim is written whole under a name of its own and then moved onto this one, so a claim
				// that is there is a claim that parses: an occupied name that will not read is one
				// somebody is moving their claim onto as this reads it. Clearing it would take the
				// project without ever asking whether the holder is alive, which is the one question this
				// whole file exists to ask - so nothing here touches it, and this caller is refused.
				if (File.Exists(path))
				{
					throw new RunnerAlreadyRunningException(
						$"The claim on this project, at '{path}', is being written or cannot be read, so " +
						$"{deploymentId} was not started. A claim that cannot be read is not deleted here: " +
						"it may be the one a live runner is holding, and a second runner on one project is " +
						"two strategies trading against each other's positions. Read the deployments of " +
						"the project to see what is running, and start again if it is not what you meant.");
				}

				// The name is empty. Nothing to honour and nothing to take over, so compete for it again.
				continue;
			}

			if (string.Equals(holder.DeploymentId, deploymentId, StringComparison.Ordinal))
			{
				// Our own claim, asked for twice. A retry after a dropped connection must not refuse the
				// caller its own project.
				var again = Home(deploymentId);

				again.Create();

				return again;
			}

			var record = Read(holder.DeploymentId);

			if (record is not null)
			{
				if (Presence(record) == RunnerProcesses.Alive)
				{
					throw new RunnerAlreadyRunningException(
						$"{holder.DeploymentId} is already running on this project, as process " +
						$"{record.ProcessId}, trading {record.Symbol} on a {record.Mode} account. Stop it " +
						"before starting another: two strategies on one account trade against each other's " +
						"positions and neither result means anything afterwards.");
				}
			}
			else if (RunnerLiveness.Of(holder.ProcessId, holder.ProcessStartedAt, holder.BootedAt, _probe)
				== RunnerProcesses.Alive)
			{
				// No runner yet and the session that took the project is still there, which is the second
				// or two between a claim and the process behind it. Refused rather than taken over: the
				// alternative loses the race to whoever asks last.
				throw new RunnerAlreadyRunningException(
					$"Session {holder.ProcessId} is starting {holder.DeploymentId} on this project right now. " +
					"One of the two of you is about to be trading against the other's positions, so this one " +
					"was not started. Read the deployments of the project in a moment to see what did.");
			}

			DiscardIfStill(path, holder);
		}

		throw new RunnerAlreadyRunningException(
			$"Another session took this project while {deploymentId} was being started. Nothing was launched. " +
			"Read the deployments of the project to see what is running, and start again if it is not what " +
			"you meant.");
	}

	/// <summary>
	/// Gives the project back, if this deployment is the one holding it.
	/// </summary>
	/// <param name="projectId">Project to release.</param>
	/// <param name="deploymentId">Deployment releasing it.</param>
	/// <remarks>
	/// Checked rather than deleted outright, and checked under the lock. A claim belongs to whoever wrote
	/// it, and a session that deleted somebody else's - one that took the project between this reading
	/// the name and clearing it - would let a second runner start on a project a first one is trading.
	/// </remarks>
	public void Release(string projectId, string deploymentId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
		ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

		var path = ClaimFile(projectId);

		using var held = Hold(path);

		if (held is not null && Holder(path) is { } holder && string.Equals(holder.DeploymentId, deploymentId, StringComparison.Ordinal))
			Discard(path);
	}

	/// <summary>
	/// Which deployment holds a project, if any does.
	/// </summary>
	/// <param name="projectId">Project to ask about.</param>
	/// <returns>The deployment, or <see langword="null"/> when nothing holds it.</returns>
	public string HolderOf(string projectId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

		return Holder(ClaimFile(projectId))?.DeploymentId;
	}

	/// <summary>
	/// Writes the claim, if nobody has written one.
	/// </summary>
	/// <param name="path">Where the claim goes.</param>
	/// <param name="claim">Who is taking the project.</param>
	/// <returns>Whether this caller got it.</returns>
	/// <remarks>
	/// Written whole somewhere else and then moved into place, rather than created in place and filled
	/// in, so there is no moment in which the claim exists and cannot be read. That moment is worth
	/// removing: a competitor who found the file unreadable would have to decide whether it was
	/// half-written or nonsense, and deciding wrong deletes a live claim.
	///
	/// Whether the name is free is asked and acted on under the lock. Moving a file onto a taken name is
	/// refused by the filesystem on Windows only: elsewhere the name is looked at and then replaced, so
	/// two callers that both found it free would both come away believing the project is theirs.
	///
	/// Each caller stages under a name of its own, so the losers of a race clean up after themselves
	/// without touching anything the winner wrote.
	/// </remarks>
	private static bool Took(string path, RunnerClaim claim)
	{
		var staging = $"{path}.{Safe(claim.DeploymentId)}.claiming";

		try
		{
			File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(claim, _claims));

			using var held = Hold(path);

			if (held is null || File.Exists(path))
			{
				Discard(staging);

				return false;
			}

			File.Move(staging, path);

			return true;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			Discard(staging);

			return false;
		}
	}

	private static RunnerClaim Holder(string path)
	{
		try
		{
			if (!File.Exists(path))
				return null;

			var claim = JsonSerializer.Deserialize<RunnerClaim>(File.ReadAllBytes(path), _claims);

			return string.IsNullOrEmpty(claim?.DeploymentId) ? null : claim;
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
		{
			// Being read while it is written, which is the moment a racing caller is in. Reported as
			// unreadable rather than as absent, and the caller discards it only after that.
			return null;
		}
	}

	/// <summary>
	/// Removes a claim found to be stale, provided it is still the same claim.
	/// </summary>
	/// <param name="path">Where the claim is.</param>
	/// <param name="stale">The claim that was judged stale.</param>
	/// <remarks>
	/// Between judging a claim stale and deleting it, a faster caller may have removed it and moved its own
	/// fresh claim onto the name; deleting by path would then remove the live one. So the claim is read
	/// again and removed under the lock.
	/// </remarks>
	private static void DiscardIfStill(string path, RunnerClaim stale)
	{
		using var held = Hold(path);

		if (held is not null && Holder(path) == stale)
			Discard(path);
	}

	/// <summary>
	/// Takes the lock every taker and every remover of a claim holds while it looks at the name and acts.
	/// </summary>
	/// <param name="path">Where the claim is.</param>
	/// <returns>The lock, or <see langword="null"/> when it could not be had in five seconds.</returns>
	/// <remarks>
	/// An open file beside the folder of claims, one for all of them: it is held for the length of a
	/// look and a move. The system releases it when its holder dies, so it cannot itself be left behind.
	/// </remarks>
	private static FileStream Hold(string path)
	{
		var guard = Path.GetDirectoryName(path) + LockExtension;

		for (var waited = 0; ; waited++)
		{
			try
			{
				return new FileStream(guard, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (Exception error) when (error is IOException or UnauthorizedAccessException)
			{
				if (waited >= 500)
					return null;

				Thread.Sleep(10);
			}
		}
	}

	private static void Discard(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			// Somebody else got there first, or is holding it open. Either way the next attempt to create
			// it decides, which is the only decision that has ever been authoritative here.
		}
	}

	/// <summary>
	/// An identifier as a file name.
	/// </summary>
	/// <param name="id">The identifier.</param>
	/// <returns>A name the filesystem accepts.</returns>
	/// <remarks>
	/// The identifiers this product issues are already letters, digits and an underscore, so this changes
	/// nothing for them. It is here because the value reaches a path, and a path built out of something a
	/// caller supplied is a path that can leave the directory it was meant to be in. A full stop is not
	/// among the characters kept, which is what stops the two that name a parent directory.
	/// </remarks>
	private static string Safe(string id)
	{
		var name = new StringBuilder(id.Length);

		foreach (var character in id)
			name.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_');

		return name.ToString();
	}

	private string ClaimFile(string projectId)
		=> Path.Combine(Directory, ClaimsFolder, Safe(projectId) + ClaimExtension);
}
