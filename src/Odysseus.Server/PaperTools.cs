namespace Odysseus.Server;

using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ModelContextProtocol.Server;

using Odysseus.Application;
using Odysseus.Domain;

/// <summary>
/// The tools that run a measured candidate against an account.
/// </summary>
[McpServerToolType]
public static class PaperTools
{
	/// <summary>
	/// Starts a measured candidate in a process of its own.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Deployment use cases.</param>
	/// <param name="projectId">Project the candidate belongs to.</param>
	/// <param name="candidateId">Candidate to deploy.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The deployment.</returns>
	[McpServerTool(Name = "deploy_candidate")]
	[Description("Run a candidate the closed data has already measured against the live paper account, " +
		"with the numbers it was measured with and a position sized by the same rule the backtests used. " +
		"This is the first thing that can contradict a backtest: a fill that never comes, a spread wider " +
		"than the one that was charged, a bar that arrives late. None of it shows up in history. " +
		"IMPORTANT: the deployment runs in a process of its own and keeps trading when you disconnect, " +
		"when this server exits, and after the machine's session ends - until something stops it. " +
		"Nothing stops it on your behalf, so whatever you start here is yours to stop with " +
		"stop_deployment, and a later session can find it again with list_deployments. Every deployment " +
		"this server starts trades on a paper account. One deployment per project: two strategies on one " +
		"account trade against each other's positions and neither result means anything afterwards.")]
	public static Task<object> DeployCandidate(
		ToolGuard guard,
		DeploymentService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the candidate.")] string candidateId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(DeployCandidate), async () =>
		{
			var deployment = await service.StartAsync(
				Ids.Project(projectId),
				Ids.Candidate(candidateId),
				Actors.Agent,
				cancellationToken);

			return new
			{
				deploymentId = deployment.Id.Value,
				candidateId,
				mode = deployment.Mode.ToString(),
				symbol = deployment.Symbol,
				volume = deployment.Volume,
				status = deployment.Status.ToString(),
				runnerProcessId = deployment.ProcessId,
				startedAt = deployment.StartedAt,
				howToRead = "It is trading now, in process " + deployment.ProcessId + ", and it will go on " +
					"trading after this conversation ends. Ask get_deployment for what it has done against " +
					"what was measured of it, and stop_deployment when you have seen enough - saying what to " +
					"do with any position it is holding. A handful of trades confirms nothing either way: the " +
					"point is whether the two disagree, not by how much.",
			};
		});

	/// <summary>
	/// Reports what a deployment has done, next to what was measured of it.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Deployment use cases.</param>
	/// <param name="projectId">Project the deployment belongs to.</param>
	/// <param name="deploymentId">Deployment to report on.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The report.</returns>
	[McpServerTool(Name = "get_deployment")]
	[Description("What a deployment has done against the broker, next to what the closed-data run said it " +
		"would do. Rates are per trading day on both sides, so a deployment up for a few days can be read " +
		"against a run measured over months. Watch two things above all: whether it trades at the rate it " +
		"was supposed to, and whether one trade comes to what one trade came to in the backtest. A " +
		"strategy that trades far less than expected is not being filled; one whose average trade is far " +
		"worse is paying costs the backtest did not charge. Read 'runner' before any of the numbers: " +
		"'attached' means the figures are current, 'gone' means the process ended and left whatever it " +
		"held as it stood, and 'unresponsive' means the process is alive and not answering - which is the " +
		"one that may still be trading and still be holding a position.")]
	public static Task<object> GetDeployment(
		ToolGuard guard,
		DeploymentService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the deployment.")] string deploymentId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(GetDeployment), async () =>
		{
			var report = await service.GetAsync(Ids.Project(projectId), Ids.Deployment(deploymentId), cancellationToken);

			return Describe(report);
		});

	/// <summary>
	/// Lists the deployments of a project.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Deployment use cases.</param>
	/// <param name="projectId">Project to list.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The deployments, newest first.</returns>
	[McpServerTool(Name = "list_deployments")]
	[Description("Every deployment this project has had, newest first, and every runner on this machine " +
		"that no row here accounts for. Two things are reported about each: what was recorded, and what " +
		"was found when the process trading it was looked for. Start here in a new session - a deployment " +
		"an earlier conversation started is still running, and this is how it is found again. A row " +
		"recorded as running whose runner is 'gone' belonged to a process that ended: nothing was stopped " +
		"in an orderly fashion, so read the account for anything it left open and call stop_deployment to " +
		"end it properly. Reading this list changes nothing.")]
	public static Task<object> ListDeployments(
		ToolGuard guard,
		DeploymentService service,
		[Description("Identifier of the project.")] string projectId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(ListDeployments), async () =>
		{
			var project = Ids.Project(projectId);
			var deployments = await service.ListAsync(project, cancellationToken);
			var runners = await service.RunnersAsync(cancellationToken);

			var accounted = deployments
				.Select(d => d.Deployment.Id.Value)
				.ToHashSet(StringComparer.Ordinal);

			return new
			{
				deployments = deployments.Select(view => new
				{
					deploymentId = view.Deployment.Id.Value,
					mode = view.Deployment.Mode.ToString(),
					candidateId = view.Deployment.Candidate.Value,
					symbol = view.Deployment.Symbol,
					recordedStatus = view.Deployment.Status.ToString(),
					status = view.EffectiveStatus.ToString(),
					runner = Runner(view.Runner),
					runnerProcessId = view.Deployment.ProcessId,
					startedAt = view.Deployment.StartedAt,
					stoppedAt = view.Deployment.StoppedAt,
					lastObservedAt = view.Deployment.LastObservedAt,
					trades = view.Deployment.Trades,
					realizedProfit = view.Deployment.RealizedProfit,
					position = view.Deployment.Position,
					note = view.Deployment.Note,
					runnerDetail = view.Runner?.Detail,
				}).ToArray(),

				// A runner this project has no row for: one whose project was exported, one started by a
				// command line against the same projects root, or one left by a build that recorded
				// differently. Named rather than hidden, because an unaccounted-for process that may be
				// holding a position is the thing most worth knowing about.
				orphanedRunners = runners
					.Where(r => !accounted.Contains(r.DeploymentId))
					.Select(r => new
					{
						deploymentId = r.DeploymentId,
						projectId = r.ProjectId,
						candidateId = r.CandidateId,
						mode = r.Mode.ToString(),
						runner = r.Status.ToString().ToLowerInvariant(),
						runnerProcessId = r.ProcessId,
						home = r.Home,
						detail = r.Detail,
					})
					.ToArray(),
			};
		});

	/// <summary>
	/// Stops a deployment.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="service">Deployment use cases.</param>
	/// <param name="projectId">Project the deployment belongs to.</param>
	/// <param name="deploymentId">Deployment to stop.</param>
	/// <param name="closePosition">Whether to close what it is holding.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The deployment as it ended.</returns>
	[McpServerTool(Name = "stop_deployment")]
	[Description("Stop a deployment. Say what to do with any position it is holding: closing one places " +
		"a trade on your behalf, and leaving one open keeps money at risk with nothing watching it. " +
		"There is no safe default, which is why this asks. A stop is a decision about the position; a " +
		"process that died is not, and nothing here turns one into the other - a deployment whose runner " +
		"is gone is recorded as interrupted with whatever it last wrote down, and its position is left " +
		"exactly as it stood. A runner whose process is alive and not answering is refused: nothing is " +
		"recorded, because recording it as stopped would say something untrue about an open position. " +
		"This cannot kill a process; that is a person's job at a terminal.")]
	public static Task<object> StopDeployment(
		ToolGuard guard,
		DeploymentService service,
		[Description("Identifier of the project.")] string projectId,
		[Description("Identifier of the deployment.")] string deploymentId,
		[Description("True to close what it is holding, false to leave the position open at the broker.")] bool closePosition,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(StopDeployment), async () =>
		{
			var deployment = await service.StopAsync(
				Ids.Project(projectId),
				Ids.Deployment(deploymentId),
				closePosition,
				Actors.Agent,
				cancellationToken);

			return new
			{
				deploymentId = deployment.Id.Value,
				mode = deployment.Mode.ToString(),
				status = deployment.Status.ToString(),
				stoppedAt = deployment.StoppedAt,
				trades = deployment.Trades,
				realizedProfit = deployment.RealizedProfit,
				position = deployment.Position,
				note = deployment.Note,
			};
		});

	/// <summary>
	/// Reads an account, either this server's own or a deployment's.
	/// </summary>
	/// <param name="guard">Turns a failure into the published error shape.</param>
	/// <param name="account">The account this server bound, as the broker reports it.</param>
	/// <param name="gateway">The connector that is bound, so the claim names the package behind it.</param>
	/// <param name="service">Deployment use cases, for reading a runner's own account.</param>
	/// <param name="deploymentId">Deployment whose account to read, or empty for this server's own.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The account, its holdings and its live orders.</returns>
	[McpServerTool(Name = "get_account_state")]
	[Description("Read an account itself: whether it may trade, what it can buy with, everything it is " +
		"holding and every order it has live. This is the broker talking, not this server - every other " +
		"number about a deployment is what was recorded while something was watching, and a deployment " +
		"nobody has spoken to since has been recording nothing. Pass a deploymentId to read the account " +
		"that deployment's own process is trading on, which is the one holding its position; pass an empty " +
		"deploymentId to read the account this server bound, which is a paper one and need not be the " +
		"same. Read this " +
		"after anything ends unexpectedly. It only reads; closing a position is not something this server " +
		"does on your behalf.")]
	public static Task<object> GetAccountState(
		ToolGuard guard,
		IPaperAccount account,
		BrokerGateway gateway,
		DeploymentService service,
		[Description("Identifier of a deployment to read through its own runner, or empty for this server's account.")] string deploymentId,
		CancellationToken cancellationToken)
		=> guard.RunAsync(nameof(GetAccountState), async () =>
		{
			var throughRunner = !string.IsNullOrWhiteSpace(deploymentId);

			var state = throughRunner
				? await service.AccountAsync(Ids.Deployment(deploymentId), cancellationToken)
				: await account.ReadAsync(cancellationToken);

			var connector = gateway.Current;

			return new
			{
				schemaVersion = 1,
				readThrough = throughRunner ? "the deployment's own runner" : "this server's connector",
				deploymentId = throughRunner ? deploymentId : null,
				observedAt = state.ObservedAt,
				broker = state.Broker,

				// Reported rather than asserted, and only about this server's own connector: a runner binds
				// its own, and what that one is is on the deployment rather than here.
				paperOnly = throughRunner ? (bool?)null : connector is not null && connector.IsPaperCapable,
				connector = throughRunner ? null : connector?.PackageId,
				connectorVersion = throughRunner ? null : connector?.PackageVersion,
				account = new
				{
					name = state.Account.Name,
					isAccountActive = state.Account.IsAccountActive,
					isTradingBlocked = state.Account.IsTradingBlocked,
					buyingPower = state.Account.BuyingPower,
					currency = state.Account.Currency,
				},
				positions = state.Positions.Select(p => new
				{
					symbol = p.Symbol,
					quantity = p.Quantity,
					averageEntryPrice = p.AverageEntryPrice,
					marketValue = p.MarketValue,
					unrealizedProfit = p.UnrealizedProfit,
				}).ToArray(),
				orders = state.Orders.Select(o => new
				{
					clientOrderId = o.ClientOrderId,
					brokerOrderId = o.BrokerOrderId,
					symbol = o.Symbol,
					side = o.Side,
					orderType = o.OrderType,
					quantity = o.Quantity,
					filledQuantity = o.FilledQuantity,
					limitPrice = o.LimitPrice,
					timeInForce = o.TimeInForce,
					status = o.Status,
					submittedAt = o.SubmittedAt,
					filledAveragePrice = o.FilledAveragePrice,
				}).ToArray(),
			};
		});

	private static object Describe(DeploymentReport report)
	{
		var d = report.Deployment;

		return new
		{
			deploymentId = d.Id.Value,

			// First, because everything below it is read differently for each.
			mode = d.Mode.ToString(),
			warning = d.Mode == TradingModes.Live
				? "This deployment is on a real account. Every order it places spends money."
				: null,
			candidateId = d.Candidate.Value,
			symbol = d.Symbol,
			volume = d.Volume,
			sizedAgainstEquity = BacktestService.StartingEquity,
			recordedStatus = d.Status.ToString(),
			status = report.EffectiveStatus.ToString(),
			runner = Runner(report.Runner),
			runnerProcessId = d.ProcessId,
			runnerDetail = report.Runner?.Detail,
			runnerHome = report.Runner?.Home,
			startedAt = d.StartedAt,
			stoppedAt = d.StoppedAt,
			lastObservedAt = d.LastObservedAt,
			tradingDays = report.TradingDays,
			observed = new
			{
				ordersPlaced = d.OrdersPlaced,
				trades = d.Trades,
				realizedProfit = d.RealizedProfit,
				position = d.Position,
				workingOrders = report.Runner?.State?.WorkingOrders,
				account = report.Runner?.State?.Account,
				tradesPerDay = report.ObservedTradesPerDay,
				averageTrade = report.ObservedAverageTrade,
			},
			expected = new
			{
				tradesPerDay = report.ExpectedTradesPerDay,
				averageTrade = report.ExpectedAverageTrade,
			},
			note = d.Note,
			howToRead = "Expected comes from the run on the closed data, per trading day of market it " +
				"covered. Observed is what the broker reported, counted over the trading days the strategy " +
				"received candles for - 'lastObservedAt' says when the numbers were last read, and " +
				"'runner' says whether anything is watching it now. Read the rates before the money: a " +
				"deployment that has not traded yet says nothing at all, and one trade says nothing much. " +
				"The size was worked out against 'sizedAgainstEquity', which is the equity every backtest " +
				"of this product assumes; if the account holds less, the position is a larger share of it " +
				"than the specification asked for.",
		};
	}

	/// <summary>
	/// What was found when the process trading a deployment was looked for.
	/// </summary>
	/// <param name="runner">What was found, or null when nothing was looked for.</param>
	/// <returns>The finding, in one word.</returns>
	private static string Runner(RunnerHandle runner)
		=> runner is null ? "notLookedFor" : runner.Status.ToString().ToLowerInvariant();
}
