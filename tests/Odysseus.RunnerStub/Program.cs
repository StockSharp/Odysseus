namespace Odysseus.RunnerStub;

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.Engine;

/// <summary>
/// A runner that misbehaves on purpose.
/// </summary>
/// <remarks>
/// The four findings a session can make about a runner - attached, gone, alive and silent, and not one
/// this build will talk to - are only real if something can produce each of them on demand. A real
/// runner is exactly the wrong thing to produce them with: three of the four need a broker account, a
/// crash, or a wait nobody would sit through.
///
/// So this is a runner in every respect a session cares about. It writes the same record into the same
/// home, listens on the same pipe, speaks the same framing and the same records, and does one wrong
/// thing per invocation.
///
/// What it is to do wrong is read out of its own home rather than off the command line or the
/// environment. The command line is not available: a launcher decides those arguments, and the point of
/// exercising the launcher is that it decides them. The environment is worse: it is per process, and two
/// tests running at once would be steering each other's stub.
///
/// It never trades anything and has no way to: it holds no connector, no credentials and no strategy.
/// What it holds is a number it calls a position, so that a stop can be seen to carry the decision about
/// one.
/// </remarks>
public static class Program
{
	/// <summary>File in the runner's home that says what this stub is to do wrong.</summary>
	/// <remarks>
	/// Three lines: the behaviour, then optionally the engine to claim and the deployment to claim. The
	/// last two exist because two of the handshake's refusals are about identity, and identity is
	/// something only the far end can get wrong.
	/// </remarks>
	public const string BehaviourFileName = "behaviour.txt";

	/// <summary>
	/// Behaves badly in the one way it was asked to.
	/// </summary>
	/// <param name="args">The runner's home, then whatever the launcher adds.</param>
	/// <returns>Process exit code.</returns>
	public static async Task<int> Main(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		if (args.Length == 0)
			return 2;

		var directory = Path.GetFullPath(args[0]);

		var home = new RunnerHome(directory, "stub");
		var plan = home.ReadPlan();

		home = new(directory, plan.DeploymentId);

		var told = Told(directory);
		var behaviour = told.Length > 0 ? told[0] : "answer";
		var engine = told.Length > 1 && told[1].Length > 0 ? told[1] : "stub-engine";
		var deployment = told.Length > 2 && told[2].Length > 0 ? told[2] : plan.DeploymentId;

		// Never written, which is what a launcher that spawned something into nothing looks like from the
		// outside: the process exists for a moment and no record of it ever appears.
		if (behaviour == "mute")
		{
			await Task.Delay(TimeSpan.FromMinutes(2));

			return 0;
		}

		home.WriteRecord(Record(plan, behaviour, engine));

		home.Append(new(
			DateTime.UtcNow,
			RunnerPhases.Trading,
			TradingModes.Paper,
			3m,
			1,
			2,
			12.5m,
			"Trading, in a manner of speaking."));

		// Announced and then gone, which is a crash: the record is left behind and the process is not
		// there. It is the one case where a session has something to read and nothing to talk to.
		if (behaviour == "crash")
			return 7;

		// Announced, listening on nothing. Its process is alive and it never answers, which is the finding
		// that must never be read as "nobody is holding a position".
		if (behaviour == "silent")
		{
			await Task.Delay(TimeSpan.FromMinutes(2));

			return 0;
		}

		using var stopping = new CancellationTokenSource(TimeSpan.FromMinutes(2));

		await ServeAsync(home, plan, behaviour, engine, deployment, stopping.Token);

		return 0;
	}

	private static string[] Told(string directory)
	{
		var path = Path.Combine(directory, BehaviourFileName);

		return File.Exists(path) ? File.ReadAllLines(path) : [];
	}

	private static RunnerRecord Record(RunnerPlan plan, string behaviour, string engine)
	{
		var probe = SystemProcessProbe.Instance;

		var booted = behaviour == "rebooted"
			// A record written before the machine last started, which is the one finding that is certain
			// without a process lookup at all.
			? probe.BootedAt - TimeSpan.FromDays(1)
			: probe.BootedAt;

		var started = probe.StartedAt(Environment.ProcessId) ?? DateTime.UtcNow;

		if (behaviour == "reused")
		{
			// A record whose process number is this one and whose start is not, which is exactly what a
			// stale record plus a recycled number looks like.
			started -= TimeSpan.FromHours(3);
		}

		return new(
			RunnerHome.Schema,
			RunnerProtocol.Version,
			engine,
			plan.DeploymentId,
			plan.ProjectId,
			plan.CandidateId,
			TradingModes.Paper,
			plan.Symbol,
			plan.Volume,
			plan.Connector?.PackageId ?? "StockSharp.Stub",
			plan.Connector?.PackageVersion ?? "1.0.0",
			"stub-account",
			plan.Pipe,
			Environment.ProcessId,
			started,
			booted,
			DateTime.UtcNow);
	}

	private static async Task ServeAsync(
		RunnerHome home,
		RunnerPlan plan,
		string behaviour,
		string engine,
		string deployment,
		CancellationToken cancellationToken)
	{
		var protocol = behaviour == "wrong-protocol" ? RunnerProtocol.Version + 1 : RunnerProtocol.Version;

		if (behaviour == "no-engine")
			engine = string.Empty;

		var position = 3m;
		var stopped = false;

		while (!cancellationToken.IsCancellationRequested)
		{
			using var pipe = new NamedPipeServerStream(
				plan.Pipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

			try
			{
				await pipe.WaitForConnectionAsync(cancellationToken);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			try
			{
				await WorkerProtocol.WriteAsync(
					pipe,
					new RunnerHello(
						protocol, engine, Environment.ProcessId, deployment, TradingModes.Paper,
						"StockSharp.Stub 1.0.0", "stub-account", DateTime.UtcNow),
					cancellationToken);

				// Greeted and then nothing, which is what a runner stuck inside its own broker call looks
				// like: the handshake succeeds and the first question never comes back.
				if (behaviour == "greet-then-hang")
				{
					await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);

					return;
				}

				while (!cancellationToken.IsCancellationRequested)
				{
					var request = await WorkerProtocol.ReadAsync<RunnerRequest>(pipe, cancellationToken);

					if (request is null)
						break;

					if (!string.Equals(request.Token, plan.Token, StringComparison.Ordinal))
					{
						await WorkerProtocol.WriteAsync(
							pipe,
							new RunnerAnswer(request.Id, false, null, null, "The token does not match."),
							cancellationToken);

						break;
					}

					if (request.Command == RunnerCommands.Stop)
					{
						stopped = true;

						if (request.ClosePosition)
							position = 0m;

						home.Append(new(
							DateTime.UtcNow,
							RunnerPhases.Stopped,
							TradingModes.Paper,
							position,
							0,
							2,
							12.5m,
							request.ClosePosition
								? "Stopped, closing what it held."
								: $"Stopped, leaving a position of {position} open at the broker."));
					}

					await WorkerProtocol.WriteAsync(pipe, Answer(request, position, stopped), cancellationToken);

					if (stopped)
					{
						// An orderly exit removes the record, which is what tells a later session that this
						// runner said goodbye rather than died.
						home.DeleteRecord();

						return;
					}
				}
			}
			catch (Exception error) when (error is IOException or OperationCanceledException)
			{
				// The client went away. Listen again.
			}
		}
	}

	private static RunnerAnswer Answer(RunnerRequest request, decimal position, bool stopped)
	{
		var state = new RunnerState(
			DateTime.UtcNow,
			stopped ? RunnerPhases.Stopped : RunnerPhases.Trading,
			TradingModes.Paper,
			!stopped,
			5,
			2,
			1,
			12.5m,
			position,
			stopped ? 0 : 1,
			"stub-account",
			null,
			null);

		if (request.Command != RunnerCommands.Account)
			return new(request.Id, true, state, null, null);

		return new(
			request.Id,
			true,
			state,
			new(
				DateTime.UtcNow,
				"stub",
				new("stub-account", true, false, 1_000m, "USD"),
				[],
				[]),
			null);
	}
}
