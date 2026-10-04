namespace Odysseus.Worker;

using Odysseus.Engine;

/// <summary>
/// The process a compiled candidate actually runs in.
/// </summary>
/// <remarks>
/// Deliberately small and deliberately stupid. It greets, then it answers one request at a time until
/// the pipe closes, and it holds no state between requests beyond the assemblies a run happened to load
/// - which is the whole reason it exists as a separate process rather than as a load context: the host
/// recycles it, and recycling a process is something the operating system will actually do.
///
/// It is given the bars of a slice as data and is never told where the projects are or what the broker
/// credentials are, so there is nothing here that could read a part of the history a run was not meant
/// to see. Standard output carries the frames, so every human-readable line goes to standard error.
/// </remarks>
public static class Program
{
	/// <summary>
	/// Serves requests until the host closes the pipe.
	/// </summary>
	/// <param name="args">Command line arguments, none of which are used.</param>
	/// <returns>Process exit code.</returns>
	public static async Task<int> Main(string[] args)
	{
		ArgumentNullException.ThrowIfNull(args);

		using var input = Console.OpenStandardInput();
		using var output = Console.OpenStandardOutput();

		using var cancellation = new CancellationTokenSource();

		await WorkerProtocol.WriteAsync(
			output,
			new WorkerHello(WorkerProtocol.Version, EngineIdentity.Current(), Environment.ProcessId),
			cancellation.Token);

		while (true)
		{
			var request = await WorkerProtocol.ReadAsync<WorkerRequest>(input, cancellation.Token);

			if (request is null)
				return 0;

			await WorkerProtocol.WriteAsync(output, await AnswerAsync(request, cancellation.Token), cancellation.Token);
		}
	}

	/// <summary>
	/// Does one thing and says what came of it.
	/// </summary>
	/// <param name="request">What was asked for.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The answer.</returns>
	/// <remarks>
	/// A candidate that throws is an answer, not a crash: it is reported as a failure of the run and the
	/// host records it exactly as it always did. The worker only dies of things the host is watching for.
	/// </remarks>
	private static async Task<WorkerAnswer> AnswerAsync(WorkerRequest request, CancellationToken cancellationToken)
	{
		try
		{
			if (request.Command == WorkerCommands.Backtest)
			{
				var outcome = await new EmulatedBacktestRunner().RunAsync(request.Backtest, cancellationToken);

				return new(request.Id, true, outcome, null, null, null);
			}

			if (request.Command == WorkerCommands.WalkForward)
			{
				var windows = await new GeneticStrategyOptimizer(request.BatchSize)
					.WalkForwardAsync(request.WalkForward, cancellationToken);

				return new(request.Id, true, null, null, windows, null);
			}

			var trials = await new GeneticStrategyOptimizer(request.BatchSize)
				.SearchAsync(request.Optimization, cancellationToken);

			return new(request.Id, true, null, trials, null, null);
		}
		catch (Exception error) when (error is not OperationCanceledException)
		{
			await Console.Error.WriteLineAsync(error.ToString());

			return new(request.Id, false, null, null, null, error.Message);
		}
	}
}
