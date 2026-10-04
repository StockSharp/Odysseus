namespace Odysseus.Runner;

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Application;
using Odysseus.Broker;
using Odysseus.Engine;

/// <summary>
/// The process one deployment trades in.
/// </summary>
/// <remarks>
/// One deployment per process, with no exceptions. A supervisor holding four strategies is a supervisor
/// whose crash takes four positions with it and whose upgrade cannot happen without stopping all four; a
/// process each costs some working set and buys a blast radius of one.
///
/// It outlives whatever started it. That is the whole point of it, and it is also the new hazard: a
/// deployment no longer ends when a session disconnects, so it ends only when somebody ends it.
///
/// Which account it may reach is decided here, once, from a file named by an environment variable, and
/// nowhere else. Nothing on the protocol carries it, no argument sets it, and the host that launches
/// runners removes the variable from every child it starts - so a runner cannot inherit live mode from
/// the shell that happened to launch a server. Unset means paper, and a variable that is set but names
/// something unreadable starts nothing at all.
///
/// Naming the file is not by itself the permission. The phrase written in it has to come back through a
/// channel the file cannot supply - typed at the terminal a person started this at, or set in
/// <see cref="LiveMandateConfirmation.PhraseVariable"/> when there is no terminal to ask at - and that
/// is demanded before the strategy is started, in the process that would place the orders.
/// </remarks>
public static class Program
{
	/// <summary>Environment variable naming the file the broker credentials are in.</summary>
	private const string KeysVariable = "ODYSSEUS_BROKER_KEYS";

	/// <summary>
	/// Trades one deployment until it is stopped.
	/// </summary>
	/// <param name="args">The runner's home directory, then any flags.</param>
	/// <returns>Process exit code.</returns>
	public static async Task<int> Main(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
		{
			await Console.Error.WriteLineAsync(
				"Usage: Odysseus.Runner <home> [--detached]. The home is a directory holding launch.json and " +
				"strategy.dll, which a host writes before starting this. Nothing here starts a deployment on " +
				"its own.");

			return 2;
		}

		var directory = Path.GetFullPath(args[0]);
		var detached = Array.IndexOf(args, RunnerLauncher.DetachedArgument) >= 0;

		RunnerPlan plan;
		RunnerHome home;

		try
		{
			var named = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

			// The plan is what says which deployment this is; the directory's own name is only what the
			// home is called until the plan has been read out of it.
			home = new(directory, string.IsNullOrEmpty(named) ? "runner" : named);
			plan = home.ReadPlan();
			home = new(directory, plan.DeploymentId);
		}
		catch (Exception error) when (error is IOException or ArgumentException or JsonException)
		{
			await Console.Error.WriteLineAsync($"There is nothing to run at '{directory}': {error.Message}");

			return 2;
		}

		// Taken before the console below is pointed at the log, because this is the one thing that still
		// has to reach a person: the phrase out of a live mandate, asked of whoever is sitting here. A
		// runner a host detached has nobody at its console even when it has one, so it is not asked and
		// its standard input is not opened at all.
		var terminal = ConsoleTerminal.Attach(detached);

		// Every human-readable line goes into the runner's own home from here on. This process may have
		// inherited a console it does not own - a server that speaks a protocol over standard output is
		// exactly such a parent - and one stray line on that stream corrupts the session for good.
		await using var log = new StreamWriter(
			new FileStream(home.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
			Encoding.UTF8) { AutoFlush = true };

		Console.SetOut(log);
		Console.SetError(log);

		using var stopping = new CancellationTokenSource();

		return await RunAsync(home, plan, detached, stopping, log, terminal);
	}

	private static async Task<int> RunAsync(
		RunnerHome home,
		RunnerPlan plan,
		bool detached,
		CancellationTokenSource stopping,
		TextWriter log,
		IOperatorTerminal terminal)
	{
		RunnerService service;

		try
		{
			var mandate = LiveMandateFile.Read(Environment.GetEnvironmentVariable(LiveMandateFile.PathVariable));

			// The other half of the environment read, and read here for the same reason: once, at
			// start-up, in the process that is going to place the orders. What it is worth is decided
			// later, against the phrase in the mandate, by the service that would start the strategy.
			var confirmation = new LiveMandateConfirmation(
				terminal, Environment.GetEnvironmentVariable(LiveMandateConfirmation.PhraseVariable));

			service = new(
				home,
				plan,
				mandate,
				confirmation,
				StockSharpConnectorFactory.Create(
					plan.ConnectorCache,
					BrokerCredentialFile.Read(Environment.GetEnvironmentVariable(KeysVariable)),
					plan.ConnectorSources,
					plan.ConnectorAllow,
					mandate),
				EngineIdentity.Current(),
				new SystemClock());

			await log.WriteLineAsync(
				$"{DateTime.UtcNow:O} {plan.DeploymentId} starting {plan.ClassName} on {plan.Symbol} - {mandate.Mode}.");
		}
		catch (Exception error)
		{
			// Nothing was started, and in the one case that matters - a mandate that was asked for and
			// could not be trusted - nothing must be. Falling back to paper here would run a strategy an
			// operator meant for a real account against a demo one, and say so in a field nobody re-reads.
			await log.WriteLineAsync($"{DateTime.UtcNow:O} nothing was started: {Said(error)}");

			return 3;
		}

		// Written before anything is connected to, so a host that dies between the spawn and the handshake
		// leaves a discoverable process rather than an orphan.
		service.Announce(SystemProcessProbe.Instance);

		Attend(service, detached, stopping, log);

		var listening = new RunnerServer(plan.Pipe, service).ListenAsync(stopping.Token);

		// Whether the strategy ever ran, which is the difference between a deployment that ended and one
		// that never began. It decides what this process exits with, and a refusal has to be told apart
		// from a run: whoever started a live runner and was refused is usually a script, and a script
		// reads the exit code.
		var started = false;

		try
		{
			await service.StartAsync(stopping.Token);

			started = true;

			await service.WatchAsync(stopping.Token);

			// Bounded, because the only thing left to wait for is a stop that is already under way. A
			// runner that hung here would keep a pipe open on a strategy nobody is watching, which reads
			// from outside exactly like one that is still trading.
			await service.Finished.WaitAsync(TimeSpan.FromMinutes(3), CancellationToken.None);
		}
		catch (OperationCanceledException)
		{
			// An interrupt at the terminal, or a stop that was already under way, arriving while this was
			// inside something that observes it. The journal already carries what came of it, and there
			// is nothing here worth adding: a cancellation is what somebody asked for, not a failure.
		}
		catch (Exception error)
		{
			await log.WriteLineAsync($"{DateTime.UtcNow:O} {plan.DeploymentId} ended: {Said(error)}");
		}

		await stopping.CancelAsync();

		try
		{
			await listening;
		}
		catch (OperationCanceledException)
		{
		}

		await service.DisposeAsync();

		// Removed on the way out, which is what makes the difference between an orderly exit and a crash
		// visible to a session that was not here for either: a record left behind is a runner that did not
		// get to say goodbye.
		home.DeleteRecord();

		await log.WriteLineAsync($"{DateTime.UtcNow:O} {plan.DeploymentId} exited.");

		// Zero is for a deployment that traded and has stopped. A runner that was refused its mandate,
		// or that nobody confirmed, never traded at all, and reporting success for it would tell whoever
		// started it that a strategy is running on a real account when nothing is.
		return started ? 0 : 3;
	}

	/// <summary>
	/// What goes into the log about something that went wrong.
	/// </summary>
	/// <param name="error">What went wrong.</param>
	/// <returns>The sentence alone for a refusal, and everything there is for anything else.</returns>
	/// <remarks>
	/// A refusal is a decision this process made, and its message is the whole of it: the person reading
	/// the log is being told what to do about a mandate, and a stack trace under that sentence says the
	/// software fell over when it did not - which is how a decision gets read as a bug and worked around.
	/// Anything else here is a defect, and a defect without its trace is a report nobody can act on.
	/// </remarks>
	private static string Said(Exception error)
		=> error is LiveMandateInvalidException ? error.Message : error.ToString();

	/// <summary>
	/// Decides what an interrupt on this process's console means.
	/// </summary>
	/// <param name="service">The deployment being served.</param>
	/// <param name="detached">Whether a host started this rather than a person at a terminal.</param>
	/// <param name="stopping">What ends the process.</param>
	/// <param name="log">Where the runner writes for a person.</param>
	/// <remarks>
	/// A runner a person started owns its terminal, and an interrupt there is that person ending this
	/// deployment: it performs an orderly stop that leaves the position exactly as it stands, because
	/// nobody said what to do about the position and closing one on a signal is trading on nobody's
	/// behalf.
	///
	/// The same interrupt is also how somebody answers the one question this process asks. Cancelling
	/// here is what reaches a runner standing at the phrase of a live mandate: the question is given up
	/// on, nothing is confirmed, and the mandate is refused - which is the same refusal as a phrase
	/// typed wrong, because neither one is somebody saying the sentence back.
	///
	/// A runner a host started shares whatever console the host had, and an interrupt on that console was
	/// meant for the host. Taking it would end a strategy nobody asked to end - which is the failure that
	/// putting the deployment in a process of its own exists to remove - so it is ignored and said so.
	///
	/// What this cannot cover is the console window being closed or the machine going down: the operating
	/// system ends the process without asking, and what survives is the record, the journal and whatever
	/// the strategy left at the broker. That is what the registry is read for afterwards.
	/// </remarks>
	private static void Attend(
		RunnerService service,
		bool detached,
		CancellationTokenSource stopping,
		TextWriter log)
	{
		Console.CancelKeyPress += (_, pressed) =>
		{
			pressed.Cancel = true;

			if (detached)
			{
				log.WriteLine(
					$"{DateTime.UtcNow:O} an interrupt arrived on a console this runner shares with the host " +
					"that started it. It was meant for the host, so it was ignored and the strategy is still " +
					"running. Stop this deployment through the product, or end this process directly.");

				return;
			}

			// Not awaited, because a console handler that blocks is a console that stops responding. The
			// process ends when the work below finishes, and the journal says what it came to.
			_ = EndAsync(service, stopping, "Stopped by an interrupt at the terminal.");
		};

		AppDomain.CurrentDomain.ProcessExit += (_, _) =>
		{
			// Best effort on the way out. It never closes the position either: whoever ended the process
			// did not say what to do about it.
			EndAsync(service, stopping, "The process was ended.").GetAwaiter().GetResult();
		};
	}

	private static async Task EndAsync(RunnerService service, CancellationTokenSource stopping, string why)
	{
		try
		{
			await service.EndAsync(why, CancellationToken.None);
		}
		catch (Exception)
		{
			// There is nowhere left to report it to, and the journal already carries whatever was written
			// before this. Swallowed rather than allowed to escape a handler that has no caller.
		}

		try
		{
			await stopping.CancelAsync();
		}
		catch (ObjectDisposedException)
		{
			// The process-exit handler can arrive after the work it would have cancelled has finished and
			// let go of this. Nothing is left to stop.
		}
	}
}
