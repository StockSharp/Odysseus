namespace StockSharp.Odysseus.Server;

using Microsoft.Extensions.Logging;

/// <summary>
/// Runs a tool body and turns a failure into the published error shape.
/// </summary>
/// <remarks>
/// Without this a failure reaches the agent as the transport's own wording — that something went wrong
/// invoking a tool — which names nothing and leaves the agent guessing. What the agent receives instead
/// is the category it can branch on, the message that says what was wrong, and what to do next.
/// </remarks>
public sealed class ToolGuard(ILogger<ToolGuard> logger)
{
	/// <summary>
	/// Runs a tool body.
	/// </summary>
	/// <typeparam name="T">Type of the successful answer.</typeparam>
	/// <param name="tool">Name of the tool, for the log.</param>
	/// <param name="body">Work to run.</param>
	/// <returns>The answer, or the failure in the published shape.</returns>
	public async Task<object> RunAsync<T>(string tool, Func<Task<T>> body)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(tool);
		ArgumentNullException.ThrowIfNull(body);

		var correlationId = Guid.NewGuid().ToString("n")[..12];

		try
		{
			return await body();
		}
		catch (Exception error)
		{
			var described = ToolErrors.Describe(error, correlationId);

			// Our own defects are logged at error level and the caller's mistakes at warning, so the log
			// separates what we have to fix from what the agent has to. A worker that died or a store that
			// would not take a result is ours: the agent is told to ask again and cannot act on it further,
			// so the only place it can be chased is here, with the exception underneath it.
			if (described.Category is ToolErrorCategories.Internal
				or ToolErrorCategories.WorkerFailure
				or ToolErrorCategories.StorageFailure)
				logger.LogError(error, "{Tool} failed [{CorrelationId}]", tool, correlationId);
			else
				logger.LogWarning("{Tool} refused: {Message} [{CorrelationId}]", tool, described.Message, correlationId);

			return new
			{
				error = new
				{
					category = described.Category.ToString(),
					message = described.Message,
					remediation = described.Remediation,
					correlationId = described.CorrelationId,
				},
			};
		}
	}

	/// <summary>
	/// Runs a tool body that produces its answer without awaiting.
	/// </summary>
	/// <typeparam name="T">Type of the successful answer.</typeparam>
	/// <param name="tool">Name of the tool, for the log.</param>
	/// <param name="body">Work to run.</param>
	/// <returns>The answer, or the failure in the published shape.</returns>
	public Task<object> Run<T>(string tool, Func<T> body)
	{
		ArgumentNullException.ThrowIfNull(body);

		return RunAsync(tool, () => Task.FromResult(body()));
	}
}
