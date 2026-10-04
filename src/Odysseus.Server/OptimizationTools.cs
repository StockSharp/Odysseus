namespace Odysseus.Server;

/// <summary>
/// The tools that search the numbers a specification declared.
/// </summary>
[McpServerToolType]
public static class OptimizationTools
{
	/// <summary>
	/// Searches a candidate's declared numbers and measures what it picked.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Optimization use cases.</param>
	/// <param name="projectId">Project the candidate belongs to.</param>
	/// <param name="candidateId">Candidate to search.</param>
	/// <param name="symbol">Symbol to trade, or empty for the first of the dataset.</param>
	/// <param name="seed">What the search's randomness starts from.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the search found.</returns>
	[McpServerTool(Name = "run_optimization")]
	[Description("Search the numbers this candidate declared and measure the setting that came out best. " +
		"The ranges come from the specification, not from this call: each number was declared with its " +
		"bounds and its step, and the search stays inside them. It runs on the development slice only — " +
		"searching on validation would tune the candidate to the data meant to judge it. Every setting " +
		"evaluated costs a backtest, so the whole search is charged up front and refused if the project " +
		"cannot afford it. The search ranks; it does not decide: what it picks is then run and judged " +
		"like anything else. Look at the spread of the results, not only at the winner — a lone peak " +
		"surrounded by poor neighbours is a number that fitted this history, not an edge.")]
	public static Task<object> RunOptimization(
		ToolGuard guard,
		OptimizationService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the candidate.")] string candidateId,
		[Description("Symbol to trade. Leave empty to trade the first symbol of the dataset.")] string symbol,
		[Description("Seed for the search, so the same search can be repeated. Any whole number.")] int seed,
		[Description("Caller-generated key identifying this request, reused on retries of the same intent.")] string operationKey,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(RunOptimization), async () =>
		{
			var outcome = await service.SearchAsync(
				Ids.Project(projectId),
				Ids.Candidate(candidateId),
				symbol,
				seed,
				operationKey,
				Actors.Agent,
				cancellationToken);

			return new
			{
				candidateId,
				seed = outcome.Seed,
				wasAlreadySearched = outcome.WasAlreadySearched,
				evaluated = outcome.Trials.Count,
				chosen = new
				{
					parameters = outcome.Chosen.Parameters,
					netProfit = outcome.Chosen.NetProfit,
					maxDrawdownPercent = outcome.Chosen.MaxDrawdownPercent,
					trades = outcome.Chosen.Trades,
					runId = outcome.Run.Id.Value,
					measured = outcome.Run.Metrics is null ? null : new
					{
						netProfit = outcome.Run.Metrics.Net.Profit,
						profitFactor = outcome.Run.Metrics.Net.ProfitFactor,
						maxDrawdownPercent = outcome.Run.Metrics.Risk.MaxDrawdownPercent,
						trades = outcome.Run.Metrics.Trades.Count,
					},
				},
				trials = outcome.Trials.Select(t => new
				{
					parameters = t.Parameters,
					netProfit = t.NetProfit,
					maxDrawdownPercent = t.MaxDrawdownPercent,
					trades = t.Trades,
				}).ToArray(),
				howToRead = outcome.WasAlreadySearched
					? "This key has been used before, so this is the answer that search produced and nothing " +
						"was charged. The settings it looked at are not kept - only the one it chose and the " +
						"run that measured it - which is why no trials come back with a replay."
					: "The winner is measured twice: once by the search, on its own scale, and once by the " +
						"ordinary run that follows, which is the measurement that counts. Where the two " +
						"disagree, the run is the one to believe. A setting whose neighbours did far worse is " +
						"a setting that fitted this stretch of history.",
			};
		});

	/// <summary>
	/// Walks a candidate's declared numbers forward over the development slice.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Optimization use cases.</param>
	/// <param name="projectId">Project the candidate belongs to.</param>
	/// <param name="candidateId">Candidate to walk forward.</param>
	/// <param name="symbol">Symbol to trade, or empty for the first of the dataset.</param>
	/// <param name="inSampleDays">Days each window is fitted on.</param>
	/// <param name="outOfSampleDays">Days each window is tested on, and the step between windows.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What each window chose and what that came to.</returns>
	[McpServerTool(Name = "run_walk_forward")]
	[Description("Walk this candidate's declared numbers forward over the development slice. The slice is cut " +
		"into windows that step forward by the tested length: each window searches every setting the " +
		"specification allows on its first stretch, takes the best, and runs it on the stretch right after, " +
		"which the choice never saw. So every tested result comes from numbers chosen without it, which is " +
		"the question run_optimization alone cannot answer. Every window costs a backtest per setting plus " +
		"one for the test, charged up front and refused if the project cannot afford it. Read the tested " +
		"results side by side: numbers that hold up window after window are a property of the instrument, " +
		"numbers that change every window and only win where they were chosen are a fit to the history.")]
	public static Task<object> RunWalkForward(
		ToolGuard guard,
		OptimizationService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the candidate.")] string candidateId,
		[Description("Symbol to trade. Leave empty to trade the first symbol of the dataset.")] string symbol,
		[Description("Days each window searches the numbers on. A whole number, at least 1.")] int inSampleDays,
		[Description("Days each window tests the chosen numbers on, and how far each window steps forward. A whole number, at least 1.")] int outOfSampleDays,
		[Description("Caller-generated key identifying this request, reused on retries of the same intent.")] string operationKey,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(RunWalkForward), async () =>
		{
			var outcome = await service.WalkForwardAsync(
				Ids.Project(projectId),
				Ids.Candidate(candidateId),
				symbol,
				inSampleDays,
				outOfSampleDays,
				operationKey,
				Actors.Agent,
				cancellationToken);

			return new
			{
				candidateId,
				wasAlreadyRun = outcome.WasAlreadyRun,
				windows = outcome.Windows.Select(w => new
				{
					fittedFrom = w.InSampleFrom,
					fittedTo = w.InSampleTo,
					testedFrom = w.OutOfSampleFrom,
					testedTo = w.OutOfSampleTo,
					parameters = w.Parameters,
					fittedScore = w.InSampleFitness,
					tested = new
					{
						netProfit = w.OutOfSample.Net.Profit,
						returnPercent = w.OutOfSample.Net.ReturnPercent,
						maxDrawdownPercent = w.OutOfSample.Risk.MaxDrawdownPercent,
						trades = w.OutOfSample.Trades.Count,
					},
				}).ToArray(),
				howToRead = outcome.WasAlreadyRun
					? "This key has been used before, so this is the answer that walk-forward produced and nothing " +
						"was charged."
					: "The tested numbers are the result; the fitted score only says what the choice was made on. " +
						"A window with no trades tested nothing. Compare the tested results with each other and " +
						"with the fitted scores: a strategy that only does well where it was fitted is fitted.",
			};
		});
}
