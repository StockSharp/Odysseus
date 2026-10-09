namespace StockSharp.Odysseus.Engine;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

using StockSharp.Odysseus.Domain;

/// <summary>
/// Where a runner is told to get a connector from.
/// </summary>
/// <param name="CacheRoot">Directory downloaded connectors are kept in.</param>
/// <param name="Sources">Feed addresses or local directories, in the order they are to be tried.</param>
/// <param name="Allow">Package identifier prefixes a runner may load; an empty list allows none.</param>
/// <remarks>
/// Passed on rather than re-decided. A runner that read the allow-list from its own environment would be
/// a second place the guard lives, and two places is one too many; a runner that inherited it would take
/// whatever the shell had, which is not what the operator configured for the server.
/// </remarks>
public sealed record RunnerConnectorPolicy(
	string CacheRoot,
	IReadOnlyList<string> Sources,
	IReadOnlyList<string> Allow);

/// <summary>
/// The runner process, seen from a session.
/// </summary>
/// <remarks>
/// A worker is started per run, serves one request at a time and is expected to end; a runner is started
/// once, outlives the process that started it, and has to be findable by a process that did not. So this
/// keeps no handle on anything: it writes a directory, starts a process, and afterwards answers every
/// question by reading that directory and connecting to what it names. A dictionary of live sessions is
/// exactly the thing being removed - it dies with the process that owns it, and everything it knew about
/// the positions somebody is holding dies with it.
///
/// It cannot kill a runner, and that is deliberate. An agent that can kill a runner has a way to leave a
/// position unattended with nothing recorded about it, which is the failure this whole arrangement exists
/// to remove. Killing is a person's, at a terminal.
/// </remarks>
public sealed class RunnerLauncher : IRunnerHost
{
	/// <summary>
	/// Argument telling a runner it was started by a host rather than by a person at a terminal.
	/// </summary>
	/// <remarks>
	/// It decides one thing: whether an interrupt on the console the process happens to share is taken as
	/// an instruction to stop. A runner a person started owns its terminal and stops when they ask; a
	/// runner a host started shares whatever console the host had, and an interrupt there was meant for
	/// the host - taking it would end a strategy nobody asked to end, which is precisely what putting the
	/// deployment in its own process was for.
	/// </remarks>
	public const string DetachedArgument = "--detached";

	private readonly RunnerOptions _options;
	private readonly RunnerRegistry _registry;
	private readonly RunnerConnectorPolicy _policy;
	private readonly string _expectedEngine;

	/// <summary>
	/// Creates the launcher.
	/// </summary>
	/// <param name="options">Where the runner is and how long it is given to answer.</param>
	/// <param name="registry">Every runner this projects root knows about.</param>
	/// <param name="policy">Where a runner is told to get a connector from.</param>
	/// <param name="expectedEngine">
	/// The platform build this session was assembled against. An empty one is a session that cannot say
	/// which build it carries, and a runner is then refused rather than trusted.
	/// </param>
	public RunnerLauncher(
		RunnerOptions options,
		RunnerRegistry registry,
		RunnerConnectorPolicy policy,
		string expectedEngine)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
		_registry = registry ?? throw new ArgumentNullException(nameof(registry));
		_policy = policy ?? throw new ArgumentNullException(nameof(policy));
		_expectedEngine = expectedEngine ?? string.Empty;
	}

	/// <summary>
	/// How a runner is launched, and what it is deliberately given and denied.
	/// </summary>
	/// <param name="options">Where the runner is.</param>
	/// <param name="home">The directory the runner is to work out of.</param>
	/// <param name="mandatePath">
	/// Path of the live mandate, when a human at a terminal asked for one, and empty otherwise.
	/// </param>
	/// <returns>The start information, ready to be started.</returns>
	/// <remarks>
	/// Public because what is put into and taken out of the environment here is a guarantee rather than a
	/// detail, and a guarantee that cannot be asserted is a hope.
	/// </remarks>
	public static ProcessStartInfo Describe(RunnerOptions options, RunnerHome home, string mandatePath)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(home);

		var runtimeHosted = options.RunnerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

		var info = new ProcessStartInfo(runtimeHosted ? "dotnet" : options.RunnerPath)
		{
			// Not redirected, and this is the one that matters most. A pipe whose reader has exited is a
			// write error on the child's first line, and the reader here exits by design - that is what
			// the runner is for. It writes to its own log in its own home instead.
			RedirectStandardInput = false,
			RedirectStandardOutput = false,
			RedirectStandardError = false,
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = Path.GetDirectoryName(options.RunnerPath) ?? AppContext.BaseDirectory,
		};

		if (runtimeHosted)
			info.ArgumentList.Add(options.RunnerPath);

		foreach (var argument in options.Arguments ?? Array.Empty<string>())
			info.ArgumentList.Add(argument);

		info.ArgumentList.Add(home.Directory);
		info.ArgumentList.Add(DetachedArgument);

		// The exact inverse of the worker, and worth saying so where it is done because it looks like a
		// mistake otherwise: WorkerHost.Describe removes these because a worker must never reach an
		// account, and they are left here because reaching an account is the whole of a runner's job.
		// The credential file is a path rather than a value for this reason - the value never crosses.

		// Live mode never arrives by inheritance. An operator who exported the variable in the shell that
		// started this host still gets paper runners from it: it is removed first and then set only from
		// what the caller explicitly passed, which the MCP server always leaves empty.
		info.Environment.Remove(LiveMandateFile.PathVariable);

		// And the confirmation of it never arrives at all. It is removed for the same reason and is never
		// set from anything here, so a runner this host starts has no phrase and no terminal to be asked
		// for one at - which is what makes a person at a terminal the only way into live mode, rather than
		// a promise that no caller will pass a mandate path.
		info.Environment.Remove(LiveMandateConfirmation.PhraseVariable);

		if (!string.IsNullOrWhiteSpace(mandatePath))
			info.Environment[LiveMandateFile.PathVariable] = mandatePath;

		// Project metadata belongs to the workspace's MCP or CLI process. A runner reads and writes only
		// its own home, which carries everything it needs.
		info.Environment.Remove("ODYSSEUS_PROJECTS_ROOT");

		return info;
	}

	/// <inheritdoc />
	public async ValueTask<RunnerHandle> LaunchAsync(RunnerLaunch launch, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(launch);

		// Taken before anything is written, so that two sessions racing over one project settle it here
		// rather than at the broker. Released again if this launch does not produce a live runner.
		var home = _registry.Claim(launch.ProjectId, launch.DeploymentId);

		var plan = Plan(launch, home);

		try
		{
			await File.WriteAllBytesAsync(home.AssemblyFile, launch.Assembly ?? [], cancellationToken);

			home.WritePlan(plan);
		}
		catch
		{
			_registry.Release(launch.ProjectId, launch.DeploymentId);
			throw;
		}

		Process process;

		try
		{
			process = Process.Start(Describe(_options, home, launch.MandatePath));
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			_registry.Release(launch.ProjectId, launch.DeploymentId);

			// A runner that is not where the deployment says it is arrives here as the operating system's
			// own error. Typed, so the answer names the path and the variable instead of saying that
			// something went wrong while a strategy was being started.
			throw new RunnerStartFailedException(
				$"The runner at {_options.RunnerPath} would not start: {error.Message} Set " +
				$"{RunnerOptions.PathVariable}, or deploy the host with its '{RunnerOptions.RunnerFolder}' " +
				"folder beside it.", error);
		}

		// Null is documented only for a reused process, which needs a shell start and this is not one.
		// Refused rather than dereferenced further in, and the project given back on the way out.
		if (process is null)
		{
			_registry.Release(launch.ProjectId, launch.DeploymentId);

			throw new RunnerStartFailedException(
				$"The runner at {_options.RunnerPath} started nothing and said nothing about why.");
		}

		try
		{
			return await GreetedAsync(launch, home, process, cancellationToken);
		}
		catch
		{
			Kill(process);

			_registry.Release(launch.ProjectId, launch.DeploymentId);

			throw;
		}
		finally
		{
			process.Dispose();
		}
	}

	/// <inheritdoc />
	public async ValueTask<RunnerHandle> AttachAsync(string deploymentId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

		var record = _registry.Read(deploymentId);

		if (record is null)
			return Nothing(deploymentId);

		if (_registry.Presence(record) == RunnerProcesses.Gone)
			return Departed(record);

		RunnerClient client = null;

		try
		{
			client = await AttachedAsync(record, cancellationToken);

			var state = await client.ObserveAsync(_options.CallDeadline, cancellationToken);

			return Attached(record, state);
		}
		catch (RunnerHandshakeException refusal)
		{
			return Strange(record, refusal.Message);
		}
		catch (Exception error) when (Unreachable(error, cancellationToken))
		{
			return Silent(record, error);
		}
		finally
		{
			if (client is not null)
				await client.DisposeAsync();
		}
	}

	/// <inheritdoc />
	public async ValueTask<RunnerHandle> StopAsync(string deploymentId, bool closePosition, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

		var record = _registry.Read(deploymentId);

		if (record is null)
		{
			// It exited in an orderly fashion and removed its own record, or it never wrote one. Either
			// way nothing is trading this project, and the claim it holds has to go back - otherwise a
			// runner that ended by itself would block the project for good.
			Release(deploymentId);

			return Nothing(deploymentId);
		}

		if (_registry.Presence(record) == RunnerProcesses.Gone)
		{
			_registry.Release(record.ProjectId, record.DeploymentId);

			return Departed(record);
		}

		RunnerClient client = null;

		try
		{
			client = await AttachedAsync(record, cancellationToken);

			var state = await client.StopAsync(closePosition, _options.StopDeadline, cancellationToken);

			// A stop that failed leaves the strategy possibly holding a position, so the project stays held.
			if (state.Phase == RunnerPhases.Stopped)
				_registry.Release(record.ProjectId, record.DeploymentId);

			return Attached(record, state);
		}
		catch (RunnerHandshakeException refusal)
		{
			// Something answered to the name and it is not this deployment. Nothing was stopped, and
			// nothing is recorded about a process this session cannot identify.
			return Strange(record, refusal.Message);
		}
		catch (Exception error) when (Unreachable(error, cancellationToken))
		{
			var last = _registry.Home(record.DeploymentId).LastEntry();

			throw new RunnerUnresponsiveException(
				$"Process {record.ProcessId} is alive and not answering. It is running {record.DeploymentId} on " +
				$"{record.Symbol} against a {record.Mode} account, and the last thing it wrote down was a " +
				$"position of {(last?.Position ?? 0m).ToString(CultureInfo.InvariantCulture)} with " +
				$"{last?.WorkingOrders ?? 0} order(s) still working. It may still be trading. Nothing was " +
				"recorded, because recording this as stopped would say something untrue about an open " +
				$"position. Its home is {_registry.Home(record.DeploymentId).Directory}.");
		}
		finally
		{
			if (client is not null)
				await client.DisposeAsync();
		}
	}

	/// <inheritdoc />
	public async ValueTask<PaperAccountState> AccountAsync(string deploymentId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

		var record = _registry.Read(deploymentId)
			?? throw new InvalidOperationException(
				$"There is no runner recorded for {deploymentId}, so there is no connection to read an account " +
				"through. get_account_state reads the connector this server bound, which is a different account.");

		await using var client = await AttachedAsync(record, cancellationToken);

		return await client.AccountAsync(_options.CallDeadline, cancellationToken);
	}

	/// <inheritdoc />
	public async ValueTask<IReadOnlyList<RunnerHandle>> ListAsync(CancellationToken cancellationToken)
	{
		var found = new List<RunnerHandle>();

		foreach (var record in _registry.Records())
		{
			cancellationToken.ThrowIfCancellationRequested();

			found.Add(await AttachAsync(record.DeploymentId, cancellationToken));
		}

		return found;
	}

	/// <summary>
	/// Whether a failure means the runner could not be reached, as opposed to the caller giving up.
	/// </summary>
	/// <param name="error">What went wrong.</param>
	/// <param name="cancellationToken">The caller's token.</param>
	/// <returns>Whether this is the runner's silence rather than the caller's cancellation.</returns>
	private static bool Unreachable(Exception error, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
			return false;

		return error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException;
	}

	private static void Kill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (Exception error) when (error is InvalidOperationException or Win32Exception)
		{
			// It had already gone, which is the outcome that was wanted. A runner is only ever killed here,
			// on the one path where it never became a deployment anybody was told about.
		}
	}

	private RunnerPlan Plan(RunnerLaunch launch, RunnerHome home)
		=> new(
			RunnerHome.Schema,
			launch.DeploymentId,
			launch.ProjectId,
			launch.CandidateId,
			launch.ClassName,
			Path.GetFileName(home.AssemblyFile),
			launch.Symbol,
			launch.TimeFrame,
			launch.Volume,
			launch.Parameters ?? new Dictionary<string, decimal>(StringComparer.Ordinal),
			launch.Connector,
			_policy.CacheRoot,
			_policy.Sources,
			_policy.Allow,
			RunnerProtocol.PipeOf(launch.DeploymentId),
			Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
			_options.Heartbeat);

	private async Task<RunnerHandle> GreetedAsync(
		RunnerLaunch launch,
		RunnerHome home,
		Process process,
		CancellationToken cancellationToken)
	{
		var waited = Stopwatch.StartNew();

		while (waited.Elapsed < _options.HandshakeDeadline)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var record = home.ReadRecord();

			if (record is not null)
			{
				var attached = await AttachAsync(launch.DeploymentId, cancellationToken);

				if (attached.Status == RunnerStatuses.Attached)
					return attached;
			}

			if (process.HasExited)
			{
				throw new RunnerStartFailedException(
					$"The runner for {launch.DeploymentId} exited with code {process.ExitCode} without trading " +
					$"anything. What it managed to say is in {home.LogFile}, and what it did is in " +
					$"{home.JournalFile}; both are left where they are.");
			}

			await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
		}

		throw new RunnerStartFailedException(
			$"The runner for {launch.DeploymentId} did not greet within " +
			$"{_options.HandshakeDeadline.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds. " +
			$"It was stopped and its home was left at {home.Directory} for inspection.");
	}

	private async Task<RunnerClient> AttachedAsync(RunnerRecord record, CancellationToken cancellationToken)
		=> await RunnerClient.ConnectAsync(
			record, Token(record.DeploymentId), _expectedEngine, _options.CallDeadline, cancellationToken);

	/// <summary>
	/// Gives back the project a deployment holds, when its own record is no longer there to name it.
	/// </summary>
	/// <param name="deploymentId">Deployment to release the project of.</param>
	/// <remarks>
	/// The plan outlives the record: a runner deletes the second on the way out and never touches the
	/// first, so this is where the project identifier still is once the runner has gone.
	/// </remarks>
	private void Release(string deploymentId)
	{
		try
		{
			_registry.Release(_registry.Home(deploymentId).ReadPlan().ProjectId, deploymentId);
		}
		catch (Exception error) when (error is IOException or JsonException or ArgumentException)
		{
			// No plan to read, so no project was ever claimed under it and there is nothing to give back.
		}
	}

	private string Token(string deploymentId)
	{
		try
		{
			return _registry.Home(deploymentId).ReadPlan().Token;
		}
		catch (Exception error) when (error is IOException or JsonException)
		{
			// Without the token the runner refuses the connection, which is reported as a runner this
			// session cannot talk to rather than as one that is not there. The difference matters: only
			// one of the two means nobody is holding a position.
			return string.Empty;
		}
	}

	private RunnerHandle Nothing(string deploymentId)
		=> new(
			deploymentId,
			string.Empty,
			string.Empty,
			RunnerStatuses.Unknown,
			TradingModes.Paper,
			0,
			string.Empty,
			_registry.Home(deploymentId).Directory,
			null,
			"No runner was ever recorded for this deployment on this projects root, so there is nothing here " +
			"to read or to stop. A runner started against another projects root is not visible from this one.");

	private RunnerHandle Departed(RunnerRecord record)
	{
		var last = _registry.Home(record.DeploymentId).LastEntry();

		var wrote = last is null
			? "It wrote nothing down before it went."
			: $"The last thing it wrote down, at {last.At:O}, was: {last.What} - holding " +
				$"{last.Position.ToString(CultureInfo.InvariantCulture)} with {last.WorkingOrders} order(s) " +
				$"still working, {last.Trades} trade(s) and " +
				$"{last.RealizedProfit.ToString(CultureInfo.InvariantCulture)} realized.";

		return Handle(record, RunnerStatuses.Gone, null, $"{_registry.Explain(record)} {wrote}");
	}

	private RunnerHandle Attached(RunnerRecord record, RunnerState state)
		=> Handle(record, RunnerStatuses.Attached, state, "Connected and answering.");

	private RunnerHandle Silent(RunnerRecord record, Exception error)
		=> Handle(
			record,
			RunnerStatuses.Unresponsive,
			null,
			$"Process {record.ProcessId} is alive and did not answer: {error.Message} It may still be holding " +
			$"a position in {record.Symbol} and it may still be trading. Nothing about it was recorded.");

	private RunnerHandle Strange(RunnerRecord record, string why)
		=> Handle(
			record,
			RunnerStatuses.Unknown,
			null,
			$"{why} Nothing was asked of it, and nothing about this deployment was recorded.");

	private RunnerHandle Handle(RunnerRecord record, RunnerStatuses status, RunnerState state, string detail)
		=> new(
			record.DeploymentId,
			record.ProjectId,
			record.CandidateId,
			status,
			record.Mode,
			record.ProcessId,
			record.Engine,
			_registry.Home(record.DeploymentId).Directory,
			state,
			detail);
}
