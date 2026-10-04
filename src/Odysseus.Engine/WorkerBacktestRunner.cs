namespace Odysseus.Engine;

using System.Threading.Tasks;

/// <summary>
/// Runs a compiled candidate in the isolated worker.
/// </summary>
/// <remarks>
/// The port is unchanged and so is what comes back: fills and an account value, measured on the same
/// emulator as before. Only where the arithmetic happens has moved, which is the point - the server no
/// longer holds an assembly it cannot unload, and a run that will not finish is stopped rather than
/// waited on.
/// </remarks>
public sealed class WorkerBacktestRunner : IBacktestRunner
{
	private readonly WorkerHost _worker;

	/// <summary>
	/// Creates the runner.
	/// </summary>
	/// <param name="worker">The worker process the run happens in.</param>
	public WorkerBacktestRunner(WorkerHost worker)
	{
		_worker = worker ?? throw new ArgumentNullException(nameof(worker));
	}

	/// <inheritdoc />
	public Task<BacktestOutcome> RunAsync(BacktestRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.Bars.Count == 0)
			throw new ArgumentException("There are no bars to run over.", nameof(request));

		return _worker.RunAsync(request, cancellationToken);
	}
}
