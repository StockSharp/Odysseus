namespace Odysseus.Server;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ModelContextProtocol.Server;

using Odysseus.Application;
using Odysseus.Domain;

/// <summary>
/// The tools that run a candidate and say what it did.
/// </summary>
[McpServerToolType]
public static class BacktestTools
{
	/// <summary>
	/// Runs a candidate over a slice of the project's history.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Backtest use cases.</param>
	/// <param name="projectId">Project the candidate belongs to.</param>
	/// <param name="candidateId">Candidate to run.</param>
	/// <param name="slice">Part of the history to run over.</param>
	/// <param name="symbol">Symbol to trade, or empty for the first of the dataset.</param>
	/// <param name="scenario">Assumptions to run under.</param>
	/// <param name="parameters">Parameter values to set, or empty for the ones the strategy declares.</param>
	/// <param name="operationKey">Key that makes a repeated call return the first result.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What the run measured.</returns>
	[McpServerTool(Name = "run_backtest")]
	[Description("Run a candidate over one part of the project's history and measure what it did. Start on " +
		"'development'; 'validation' is where a candidate is checked against data it was not tuned on, so " +
		"running it repeatedly while still adjusting the rules is how a validation result stops meaning " +
		"anything. Costs are charged from the first run — a candidate that only works without them is not " +
		"a candidate. A run that has already been done with the same candidate, data, slice and scenario " +
		"is returned rather than repeated, and does not spend the allowance again.")]
	public static Task<object> RunBacktest(
		ToolGuard guard,
		BacktestService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the candidate, as returned by build_candidate.")] string candidateId,
		[Description("Part of the history: 'development' or 'validation'.")] string slice,
		[Description("Symbol to trade. Leave empty to trade the first symbol of the dataset.")] string symbol,
		[Description("What the run is measured under. 'baseline' charges the ordinary costs and fills on " +
			"the bar after the one the rule fired on. 'costsX15' charges costs half again as high, which " +
			"asks whether the result survives the assumption a live account is most likely to contradict. " +
			"'entryOneBarLater' keeps the ordinary costs but waits a further bar before entering, which " +
			"asks what the result is worth without the first price the signal could have been acted on — " +
			"the doubt a backtest getting there first cannot settle. Leave empty for 'baseline'.")] string scenario,
		[Description("Parameter values, as 'Name=Value' separated by commas. Leave empty to use what the strategy declares.")] string parameters,
		[Description("Caller-generated key identifying this request, reused on retries of the same intent.")] string operationKey,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(RunBacktest), async () =>
		{
			var (run, wasAlreadyRun) = await service.RunAsync(
				Ids.Project(projectId),
				Ids.Candidate(candidateId),
				ParseSlice(slice),
				RunWindow.Whole,
				symbol,
				RunScenario.Parse(scenario),
				ParseParameters(parameters),
				operationKey,
				Actors.Agent,
				cancellationToken);

			var untouched = await service.SymbolsNotCoveredAsync(
				Ids.Project(projectId), run.Symbol, cancellationToken);

			return Describe(run, wasAlreadyRun, untouched);
		});

	/// <summary>
	/// Reads what a run measured.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Backtest use cases.</param>
	/// <param name="projectId">Project the run belongs to.</param>
	/// <param name="runId">Run to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The measurements.</returns>
	[McpServerTool(Name = "get_metrics")]
	[Description("Read what a run measured: the result before and after costs, the drawdown, the trade " +
		"population, and how much of the result rests on a single trade. Read the net side, which is the " +
		"one an account would have seen, and read the concentration beside it, because a result carried " +
		"by one trade is one observation rather than an edge.")]
	public static Task<object> GetMetrics(
		ToolGuard guard,
		BacktestService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the run.")] string runId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(GetMetrics), async () =>
			Describe(await service.GetAsync(Ids.Project(projectId), Ids.Run(runId), cancellationToken), wasAlreadyRun: true));

	/// <summary>
	/// Reads the trades of a run.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Backtest use cases.</param>
	/// <param name="projectId">Project the run belongs to.</param>
	/// <param name="runId">Run to read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The trades.</returns>
	[McpServerTool(Name = "get_trades")]
	[Description("Read the individual trades of a run, in the order they closed: when each opened and " +
		"closed, at what price, and what it made after costs. Use this when a metric is surprising — a " +
		"high win rate with a poor result, or a profit that arrived in one week — because the trade list " +
		"says which it is and a summary cannot.")]
	public static Task<object> GetTrades(
		ToolGuard guard,
		BacktestService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the run.")] string runId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(GetTrades), async () =>
		{
			var trades = await service.ReadTradesAsync(Ids.Project(projectId), Ids.Run(runId), cancellationToken);

			return new
			{
				trades = trades.Select(t => new
				{
					tradeId = t.Id,
					symbol = t.Symbol,
					direction = t.Direction.ToString(),
					entryTime = t.EntryTime,
					entryPrice = t.EntryPrice,
					exitTime = t.ExitTime,
					exitPrice = t.ExitPrice,
					volume = t.Volume,
					gross = t.Gross,
					net = t.Net,
					commission = t.Commission,
					spread = t.Slippage,
					holdingMinutes = Math.Round(t.Holding.TotalMinutes, 2),
				}).ToArray(),
			};
		});

	/// <summary>
	/// Lists the runs of a project.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Backtest use cases.</param>
	/// <param name="projectId">Project to list.</param>
	/// <param name="candidateId">Candidate to list the runs of, or empty for all of them.</param>
	/// <param name="offset">How many to skip.</param>
	/// <param name="limit">How many to return.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The runs, oldest first.</returns>
	[McpServerTool(Name = "list_runs")]
	[Description("List what this project has already run, oldest first. Read it before running anything: " +
		"the allowance is finite, and a slice already measured under the same scenario will answer with " +
		"the same numbers. A project may hold hundreds of runs, so this answers a page at a time and says " +
		"how to ask for the next.")]
	public static Task<object> ListRuns(
		ToolGuard guard,
		BacktestService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of a candidate. Leave empty to list every run of the project.")] string candidateId,
		CancellationToken cancellationToken,
		[Description(Page.OffsetDescription)] int offset = 0,
		[Description(Page.LimitDescription)] int limit = 0)
		=> guard.RunAsync(nameof(ListRuns), async () =>
		{
			var all = await service.ListAsync(
				Ids.Project(projectId),
				string.IsNullOrWhiteSpace(candidateId) ? default : Ids.Candidate(candidateId),
				cancellationToken);

			var (runs, window) = Page.Of(all, offset, limit, r => new
			{
				runId = r.Id.Value,
				candidateId = r.Candidate.Value,
				slice = r.Slice.ToString(),
				symbol = r.Symbol,
				scenario = r.Scenario,
				status = r.Status.ToString(),
				trades = r.Metrics?.Trades.Count,
				netProfit = r.Metrics?.Net.Profit,
				finishedAt = r.FinishedAt,
			});

			return new { runs, window };
		});

	/// <summary>
	/// Cuts a run's result apart to see where it came from.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Backtest use cases.</param>
	/// <param name="projectId">Project the run belongs to.</param>
	/// <param name="runId">Run to explain.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The result, cut four ways.</returns>
	[McpServerTool(Name = "explain_run")]
	[Description("Cut a run's result apart: month by month, by the part of the session a position was " +
		"opened in, by how long positions were held, and by direction. A run reports one number for a " +
		"whole stretch of history, and one number cannot tell an edge that held throughout from a single " +
		"fortnight that paid for everything around it - which is the common case. Read netWithoutBestMonth " +
		"first: a candidate that turns negative there made its money in one stretch of the market rather " +
		"than out of something repeatable. Nothing is re-run and nothing is charged, so ask freely; these " +
		"are the trades the run already recorded, grouped.")]
	public static Task<object> ExplainRun(
		ToolGuard guard,
		BacktestService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the run.")] string runId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(ExplainRun), async () =>
		{
			var breakdown = await service.ExplainAsync(Ids.Project(projectId), Ids.Run(runId), cancellationToken);

			static object[] Describe(IReadOnlyList<RunSegment> segments)
				=> segments.Select(s => new
				{
					name = s.Name,
					trades = s.Trades,
					netProfit = s.Net,
					winRatePercent = s.WinRatePercent,
					shareOfNetPercent = s.ShareOfNetPercent,
				}).ToArray();

			return new
			{
				runId,
				byMonth = Describe(breakdown.ByMonth),
				byPartOfSession = Describe(breakdown.ByPartOfSession),
				byHoldingTime = Describe(breakdown.ByHoldingTime),
				byDirection = Describe(breakdown.ByDirection),
				netWithoutBestMonth = breakdown.NetWithoutBestMonth,
				monthsTraded = breakdown.MonthsTraded,
				monthsInProfit = breakdown.MonthsInProfit,
				howToRead = "Every cut is of the same trades, so each one adds back up to the run. A share " +
					"is shown only for a run that made money: a share of a loss reads like a share of a " +
					"profit and means the opposite. Holding times are cut where this run's own trades fall " +
					"rather than at round numbers, so the quarters mean something whether it holds minutes " +
					"or weeks.",
			};
		});

	private static object Describe(RunResult run, bool wasAlreadyRun, IReadOnlyList<string> untouched = null)
	{
		if (run.Status != RunStatuses.Completed)
		{
			return new
			{
				runId = run.Id.Value,
				candidateId = run.Candidate.Value,
				status = run.Status.ToString(),
				error = run.Error,
				note = run.Status == RunStatuses.Interrupted
					? "This server's own machinery failed before the candidate could answer, so the attempt " +
						"says nothing about the strategy. It is recorded so that it can be seen and counted, " +
						"and no backtest was charged for it."
					: "The run did not finish, so there is nothing to measure. The attempt is recorded and " +
						"has been charged, because a candidate that cannot be run has been answered.",
			};
		}

		var m = run.Metrics;

		return new
		{
			runId = run.Id.Value,
			candidateId = run.Candidate.Value,
			slice = run.Slice.ToString(),
			symbol = run.Symbol,
			scenario = run.Scenario,
			status = run.Status.ToString(),
			wasAlreadyRun,
			barsProcessed = run.BarsProcessed,
			whyNothingHappened = run.Diagnosis,
			startingEquity = BacktestService.StartingEquity,

			// A run trades one instrument. When the dataset holds others, saying so here is the only place
			// a caller finds out that half of what it imported was never measured.
			symbolsNotCovered = untouched is { Count: > 0 } ? untouched : null,
			gross = Describe(m.Gross),
			net = Describe(m.Net),
			costs = new
			{
				commission = m.Costs.Commission,
				spread = m.Costs.Slippage,
				total = m.Costs.Total,
				howToRead = "Fees and the spread are charged separately. Only one of them can be traded " +
					"around: a strategy can hold longer to pay the spread less often, but the fees follow " +
					"the volume either way.",
			},
			risk = new
			{
				maxDrawdownPercent = m.Risk.MaxDrawdownPercent,
				maxDrawdownAmount = m.Risk.MaxDrawdownAmount,
				recoveryFactor = m.Risk.RecoveryFactor,
			},
			trades = new
			{
				count = m.Trades.Count,
				winRatePercent = m.Trades.WinRatePercent,
				averageHoldingMinutes = m.Trades.AverageHoldingMinutes,
			},
			activity = new
			{
				turnover = m.Activity.Turnover,
				exposurePercent = m.Activity.ExposurePercent,
			},
			concentration = new
			{
				largestTradeProfitSharePercent = m.Concentration.LargestTradeProfitSharePercent,
				largestTradeId = m.Concentration.LargestTradeId,
				largestSymbolProfitSharePercent = m.Concentration.LargestSymbolProfitSharePercent,
				largestSymbol = m.Concentration.LargestSymbol,
			},
			executionErrors = m.ExecutionErrorCount,
		};
	}

	private static object Describe(ProfitBlock block)
		=> new
		{
			profit = block.Profit,
			returnPercent = block.ReturnPercent,
			profitFactor = block.ProfitFactor,
			averageTrade = block.AverageTrade,
		};

	private static DataSlices ParseSlice(string value)
	{
		var text = (value ?? string.Empty).Trim();

		if (string.IsNullOrEmpty(text))
			return DataSlices.Development;

		return Enum.TryParse<DataSlices>(text, ignoreCase: true, out var slice)
			? slice
			: throw new ArgumentException(
				$"'{value}' is not a part of the history. Use 'development' or 'validation'.",
				nameof(value));
	}

	internal static IReadOnlyDictionary<string, decimal> ParseParameters(string value)
	{
		var parsed = new Dictionary<string, decimal>(StringComparer.Ordinal);

		foreach (var pair in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);

			if (parts.Length != 2 || !decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
			{
				throw new ArgumentException(
					$"'{pair}' is not a parameter setting. Write them as 'Name=Value', separated by commas, " +
					"for example 'BreakoutPeriod=30,AtrMultiplier=2.5'.",
					nameof(value));
			}

			parsed[parts[0]] = number;
		}

		return parsed;
	}
}
