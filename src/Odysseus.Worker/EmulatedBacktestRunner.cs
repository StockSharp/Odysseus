namespace StockSharp.Odysseus.Worker;

using System.Reflection;

using StockSharp.Algo.Testing;
using StockSharp.BusinessEntities;

using StockSharp.Odysseus.Domain;
using StockSharp.Odysseus.Evaluation;

/// <summary>
/// Runs a compiled candidate through the StockSharp market emulator.
/// </summary>
/// <remarks>
/// The emulator is pointed at the shared market-data storage and the range of the run, and opens the
/// storage for reading only. A run is a measurement, and a measurement that leaves files behind is one
/// that can be contaminated by the last one; nothing here writes, so a hundred runs of an optimisation
/// change nothing about the bars all hundred were measured on.
///
/// What comes back is fills and an account value, not a verdict. The emulator is the only thing here
/// that knows about order books and matching, and the only thing that should: everything downstream
/// measures trades, and trades are the same whether they came from an emulator, a paper account or a
/// real one.
/// </remarks>
internal sealed class EmulatedBacktestRunner : IBacktestRunner
{
	/// <inheritdoc />
	public async Task<BacktestOutcome> RunAsync(BacktestRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.Bars.Count == 0)
			throw new ArgumentException("There are no bars to run over.", nameof(request));

		var security = EmulationSetup.CreateSecurity(request.Symbol, request.PriceStep);
		var portfolio = EmulationSetup.CreatePortfolio(request.StartingEquity);

		var storage = EmulationSetup.Open(request.Bars.Folder);

		var from = request.Bars.From;
		var to = request.Bars.To;

		using var connector = new HistoryEmulationConnector(
			new CollectionSecurityProvider([security]),
			new CollectionPortfolioProvider([portfolio]),
			storage)
		{
			HistoryMessageAdapter =
			{
				StartDate = from,
				StopDate = to,
			},
		};

		// Fees and the spread are charged together, because the emulator has one place to charge from.
		// They are told apart again on the way out, where the split is known exactly.
		connector.EmulationSettings.CommissionRules = [new CommissionTradeVolumeRule { Value = request.Costs.PerUnit }];

		// A bar holds what was traded in it and no more, so an order larger than that does not fill.
		connector.EmulationSettings.IncreaseDepthVolume = false;

		// The spread is charged above, in fractions of a cent the book cannot quote; a book with a spread of
		// its own would charge it a second time.
		connector.EmulationSettings.SpreadSize = 0;

		var strategy = StrategyLoader.Instantiate(
			request.Assembly,
			request.ClassName,
			request.TimeFrame,
			request.Parameters,
			request.EntryDelayBars);

		strategy.Security = security;
		strategy.Portfolio = portfolio;
		strategy.Volume = request.Volume;
		strategy.Connector = connector;

		// A rule still waiting when the history runs out would hold the run open forever, and there is no
		// later bar for it to wait for.
		strategy.WaitRulesOnStop = false;

		var fills = new List<Fill>();
		var equity = new List<EquityPoint>();
		var failures = 0;
		var bars = 0;

		// Every update of an order arrives here, so the orders themselves are collected rather than counted.
		var orders = new HashSet<Order>();

		strategy.OwnTradeReceived += (_, trade) => fills.Add(Describe(trade, request));
		strategy.OrderRegisterFailReceived += (_, _) => Interlocked.Increment(ref failures);
		strategy.OrderReceived += (_, order) => orders.Add(order);
		strategy.CandleReceived += (_, _) => bars++;

		strategy.PnLReceived2 += (_, _, time, realized, unrealized, _) =>
			equity.Add(new(Utc(time), request.StartingEquity + realized + (unrealized ?? 0m)));

		strategy.Reset();

		var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		connector.StateChanged2 += state =>
		{
			if (state == ChannelStates.Stopped)
				finished.TrySetResult();
		};

		await strategy.StartAsync(cancellationToken);

		connector.Connect();

		await connector.StartAsync(cancellationToken);
		await finished.Task.WaitAsync(cancellationToken);
		await strategy.StopAsync(cancellationToken);

		if (strategy.LastError is { } error)
			throw new InvalidOperationException($"The candidate failed while running: {error.Message}", error);

		// The account value is only reported when something moves it, so a run that never traded would
		// come back with an empty curve and no starting point to measure a drawdown from.
		if (equity.Count == 0)
			equity.Add(new(from, request.StartingEquity));

		return new(RoundTripBuilder.Build(fills), equity, bars, failures, orders.Count);
	}

	// Everything the engine reports is already UTC; a moment that says nothing about its zone is taken
	// at its word rather than shifted by whatever zone the machine happens to be in.
	private static DateTime Utc(DateTime time)
		=> time.Kind switch
		{
			DateTimeKind.Utc => time,
			DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
			_ => time.ToUniversalTime(),
		};

	private static Fill Describe(MyTrade trade, BacktestRequest request)
	{
		var charged = trade.Commission ?? 0m;

		// The engine charged fees and spread as one number. Splitting it back by the ratio it was made
		// of is exact rather than estimated, and it is what lets a report say how much of the result the
		// spread took — the number a reader wants when a candidate is marginal.
		var spread = request.Costs.PerUnit == 0
			? 0m
			: charged * (request.Costs.HalfSpread / request.Costs.PerUnit);

		return new(
			request.Symbol,
			trade.Order.Side == Sides.Buy ? TradeDirections.Long : TradeDirections.Short,
			Utc(trade.Trade.ServerTime),
			trade.Trade.Price,
			trade.Trade.Volume,
			charged - spread,
			spread);
	}
}
