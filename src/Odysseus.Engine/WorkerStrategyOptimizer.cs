namespace Odysseus.Engine;

using System.Threading.Tasks;

/// <summary>
/// Searches a candidate's declared numbers in the isolated worker.
/// </summary>
/// <remarks>
/// The whole search is one request. The platform's genetic optimizer drives a live strategy through a
/// fitness callback it invokes per setting, so there is no seam at which this could keep the search here
/// and ship the evaluations out - and no reason to want one, because a search is where the cost of
/// starting a process is amortised best: it claims fifty backtests up front and all fifty happen inside
/// one invocation.
/// </remarks>
public sealed class WorkerStrategyOptimizer : IStrategyOptimizer
{
	private readonly WorkerHost _worker;

	/// <summary>
	/// Creates the optimizer.
	/// </summary>
	/// <param name="worker">The worker process the search happens in.</param>
	public WorkerStrategyOptimizer(WorkerHost worker)
	{
		_worker = worker ?? throw new ArgumentNullException(nameof(worker));
	}

	/// <inheritdoc />
	public Task<IReadOnlyList<OptimizationTrial>> SearchAsync(
		OptimizationRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.Bars.Count == 0)
			throw new ArgumentException("There are no bars to search over.", nameof(request));

		return _worker.SearchAsync(request, cancellationToken);
	}

	/// <inheritdoc />
	public Task<IReadOnlyList<WalkForwardWindowResult>> WalkForwardAsync(
		WalkForwardRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.Bars.Count == 0)
			throw new ArgumentException("There are no bars to walk forward over.", nameof(request));

		return _worker.WalkForwardAsync(request, cancellationToken);
	}
}
