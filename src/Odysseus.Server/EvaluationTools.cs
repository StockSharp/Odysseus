namespace Odysseus.Server;

/// <summary>
/// The tool that measures a candidate, and the one that reads the measurement back.
/// </summary>
[McpServerToolType]
public static class EvaluationTools
{
	/// <summary>
	/// Puts a candidate through a fixed set of runs and reports what they came to.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Evaluation use cases.</param>
	/// <param name="projectId">Project the candidate belongs to.</param>
	/// <param name="candidateId">Candidate to measure.</param>
	/// <param name="symbol">Symbol to trade, or empty for the first of the dataset.</param>
	/// <param name="parameters">Parameter values to set, or empty for the ones the strategy declares.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The numbers from every run.</returns>
	[McpServerTool(Name = "evaluate_candidate")]
	[Description("Run a candidate six times — development, validation, validation with costs half again " +
		"as high, and three consecutive walk-forward windows — and report what each one came to. Which " +
		"runs those are is fixed, so the evidence cannot be chosen to suit the answer. All six fill on " +
		"the bar the signal fired on; whether the result survives an entry a bar later is a separate " +
		"question, asked with run_backtest under the 'entryOneBarLater' scenario. The result is " +
		"statistics: profit, drawdown, trade counts, win rate, exposure, costs, concentration, and the " +
		"spread between windows. It contains no verdict and no score, because deciding whether these " +
		"numbers are worth trading is yours to do, not the server's. The closed part of the history is " +
		"not touched here.")]
	public static Task<object> EvaluateCandidate(
		ToolGuard guard,
		EvaluationService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the candidate.")] string candidateId,
		[Description("Symbol to trade. Leave empty to trade the first symbol of the dataset.")] string symbol,
		[Description("Parameter values, as 'Name=Value' separated by commas. Leave empty to use what the strategy declares.")] string parameters,
		[Description("Caller-generated key identifying this request, reused on retries of the same intent.")] string operationKey,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(EvaluateCandidate), async () => MeasurementView.Of(await service.MeasureAsync(
			Ids.Project(projectId),
			Ids.Candidate(candidateId),
			symbol,
			BacktestTools.ParseParameters(parameters),
			operationKey,
			Actors.Agent,
			cancellationToken)));

	/// <summary>
	/// Reads the latest measurement of a candidate.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Evaluation use cases.</param>
	/// <param name="projectId">Project the candidate belongs to.</param>
	/// <param name="candidateId">Candidate to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The measurement, or a note that there is none.</returns>
	[McpServerTool(Name = "get_evaluation")]
	[Description("Read the numbers already recorded for a candidate: every run, every figure, and how " +
		"many times it has been measured. Nothing is re-run and nothing is spent.")]
	public static Task<object> GetEvaluation(
		ToolGuard guard,
		EvaluationService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the candidate.")] string candidateId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(GetEvaluation), async () =>
		{
			var measured = await service.FindAsync(
				Ids.Project(projectId), Ids.Candidate(candidateId), cancellationToken);

			return measured is null
				? new
				{
					candidateId,
					measurement = (object)null,
					note = "This candidate has not been measured. Run evaluate_candidate.",
				}
				: MeasurementView.Of(measured);
		});
}
