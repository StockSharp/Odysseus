namespace Odysseus.WorkerStub;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Engine;

/// <summary>
/// A worker that misbehaves on purpose.
/// </summary>
/// <remarks>
/// The three failure categories the error contract publishes - a run stopped on its deadline, a run
/// stopped on its memory limit, and a worker that failed - could not be produced by anything before,
/// because there was no worker. They are only real if something can make them happen on demand, and a
/// real worker running a real strategy is exactly the wrong thing to make them happen with: a test that
/// waits five minutes for a genuine timeout is a test nobody runs.
///
/// So this is a worker in every respect the host cares about - it speaks the same framing and the same
/// records - and it does one wrong thing per invocation, named by its first argument.
/// </remarks>
public static class Program
{
	/// <summary>Environment variable the stub reports as its engine, so the handshake can be steered.</summary>
	public const string EngineVariable = "ODYSSEUS_STUB_ENGINE";

	/// <summary>
	/// Behaves badly in the one way it was asked to.
	/// </summary>
	/// <param name="args">The behaviour to perform.</param>
	/// <returns>Process exit code.</returns>
	public static async Task<int> Main(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		var behaviour = args.Length > 0 ? args[0] : "answer";

		using var input = Console.OpenStandardInput();
		using var output = Console.OpenStandardOutput();

		var token = CancellationToken.None;

		if (behaviour == "silent")
			return 3;

		if (behaviour == "mute")
		{
			await Task.Delay(TimeSpan.FromMinutes(5), token);

			return 0;
		}

		var protocol = behaviour == "wrong-protocol" ? WorkerProtocol.Version + 1 : WorkerProtocol.Version;
		var engine = Environment.GetEnvironmentVariable(EngineVariable) ?? "stub-engine";

		if (behaviour == "wrong-engine")
			engine += "-elsewhere";

		// A deployment whose dependency manifests say nothing about the platform, which is what the
		// identity reader hands back as an empty string.
		if (behaviour == "no-engine")
			engine = string.Empty;

		await WorkerProtocol.WriteAsync(output, new WorkerHello(protocol, engine, Environment.ProcessId), token);

		while (true)
		{
			var request = await WorkerProtocol.ReadAsync<WorkerRequest>(input, token);

			if (request is null)
				return 0;

			switch (behaviour)
			{
				case "crash":
					return 7;

				case "hang":
					await Task.Delay(TimeSpan.FromMinutes(5), token);

					return 0;

				case "hog":
					Consume();

					return 0;

				case "wrong-id":
					// One frame too many, which is the only way an answer to another request can arrive.
					// The host reads the first as this request's, and finds the second waiting when it
					// asks the next question - so the second request is offered the first one's outcome,
					// whole and well formed and carrying the first one's identifier.
					for (var extra = 0; extra < 2; extra++)
					{
						await WorkerProtocol.WriteAsync(
							output,
							new WorkerAnswer(request.Id, true, new([], [], 1, 0, 0), [], null, null),
							token);
					}

					break;

				case "refuse":
					await WorkerProtocol.WriteAsync(
						output,
						new WorkerAnswer(request.Id, false, null, null, null, "the candidate failed while running: no."),
						token);

					break;

				default:
					await WorkerProtocol.WriteAsync(
						output,
						new WorkerAnswer(request.Id, true, new([], [], 1, 0, 0), [], null, null),
						token);

					break;
			}
		}
	}

	/// <summary>
	/// Takes memory and holds it, so the host's limit is what stops the process rather than the machine.
	/// </summary>
	private static void Consume()
	{
		var held = new List<byte[]>();

		while (true)
		{
			var block = new byte[64 * 1024 * 1024];

			// Touched, because untouched pages are not in the working set the host is watching.
			for (var i = 0; i < block.Length; i += 4096)
				block[i] = 1;

			held.Add(block);
		}
	}
}
