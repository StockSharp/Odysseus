namespace Odysseus.Broker;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Ecng.Common;

using StockSharp.Algo;
using StockSharp.Algo.Strategies;
using StockSharp.BusinessEntities;
using StockSharp.Messages;

using Odysseus.Application;
using Odysseus.Domain;
using Odysseus.Evaluation;
using Odysseus.Platform;

/// <summary>
/// Runs a compiled candidate against the broker's paper account.
/// </summary>
/// <remarks>
/// The strategy is the same assembly the backtest ran, instantiated the same way and given the same
/// numbers. Only what is underneath it changes: a live connection instead of the emulator. That is the
/// point - everything the emulator had to assume is now answered by the venue, and where the two differ
/// the venue is right.
///
/// This is the one place in the product where a compiled candidate is loaded into a long-lived process
/// rather than into the worker. It is deliberate and it is narrow: a deployment is one assembly, once,
/// held for as long as it trades, against four hundred backtests in a research session. Isolating it
/// would mean a stateful worker per deployment and an answer to what happens to an open position when
/// that worker dies, and neither is worth having for one assembly.
/// </remarks>
internal sealed class StockSharpPaperTrader : IPaperTrader, IPaperAccount
{
	private readonly IAdapterSource _adapters;

	/// <summary>How long the venue is given to answer before starting is given up on.</summary>
	private static readonly TimeSpan _patience = TimeSpan.FromMinutes(2);

	/// <summary>How long a reading waits for the holdings that follow the account.</summary>
	private static readonly TimeSpan _settle = TimeSpan.FromSeconds(3);

	/// <summary>
	/// Creates the trader.
	/// </summary>
	/// <param name="adapters">Where the configured adapter comes from.</param>
	public StockSharpPaperTrader(IAdapterSource adapters)
	{
		_adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));

		Name = $"{adapters.SourceName}-paper";
	}

	/// <inheritdoc />
	public string Name { get; }

	/// <inheritdoc />
	public async Task<IPaperSession> StartAsync(PaperRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		var connector = Open();

		try
		{
			await Connect(connector, cancellationToken);

			var security = await Find(connector, request.Symbol, cancellationToken);
			var portfolio = await Account(connector, cancellationToken);

			var strategy = StrategyLoader.Instantiate(
				request.Assembly,
				request.ClassName,
				request.TimeFrame,
				request.Parameters,
				entryDelayBars: 0);

			strategy.Security = security;
			strategy.Portfolio = portfolio;
			strategy.Volume = request.Volume;
			strategy.Connector = connector;

			var session = new Session(connector, strategy, request.Symbol, portfolio.Name);

			await strategy.StartAsync(cancellationToken);
			await Started(strategy, cancellationToken);

			return session;
		}
		catch
		{
			connector.Dispose();
			throw;
		}
	}

	/// <inheritdoc />
	/// <remarks>
	/// A connection of its own, opened and closed for the reading. It is not on the path of anything that
	/// trades, so it costs a few seconds and cannot disturb a deployment that is running.
	///
	/// What comes back is a snapshot after a short settle: the account answers first and its holdings
	/// follow, and there is no message that says 'that was all of them'. So the window is bounded rather
	/// than waited on, and the moment it was taken is part of the answer.
	/// </remarks>
	public async ValueTask<PaperAccountState> ReadAsync(CancellationToken cancellationToken)
	{
		using var connector = Open();

		var positions = new List<Position>();
		var orders = new List<Order>();

		void OnPosition(Subscription _, Position position) => positions.Add(position);
		void OnOrder(Subscription _, Order order) => orders.Add(order);

		connector.PositionReceived += OnPosition;
		connector.OrderReceived += OnOrder;

		try
		{
			await Connect(connector, cancellationToken);

			var portfolio = await Account(connector, cancellationToken);

			// Everything the account has live, which is a different question from what it holds: an order
			// that has not filled moves no position and is still money committed.
			connector.Subscribe(new(DataType.Transactions, (Security)null));

			await Task.Delay(_settle, cancellationToken);

			return new(
				DateTime.UtcNow,
				Name,
				new(
					portfolio.Name ?? string.Empty,
					portfolio.State != PortfolioStates.Blocked,
					portfolio.State == PortfolioStates.Blocked,
					portfolio.BuyOrdersMargin ?? 0m,
					portfolio.Currency?.ToString() ?? "USD"),
				[.. positions
					.Where(p => p.Security is not null && p.CurrentValue is not null and not 0m)
					.Select(Describe)],
				[.. orders.Where(o => o.State is OrderStates.Active or OrderStates.Pending).Select(Describe)]);
		}
		finally
		{
			connector.PositionReceived -= OnPosition;
			connector.OrderReceived -= OnOrder;

			connector.Disconnect();
		}
	}

	private static AccountPosition Describe(Position position)
		=> new(
			position.Security.Code,
			position.CurrentValue ?? 0m,
			position.AveragePrice ?? 0m,
			(position.CurrentPrice ?? 0m) * Math.Abs(position.CurrentValue ?? 0m),
			position.UnrealizedPnL);

	private static AccountOrder Describe(Order order)
		=> new(
			order.TransactionId.To<string>(),
			order.StringId ?? order.Id?.To<string>() ?? string.Empty,
			order.Security?.Code ?? string.Empty,
			order.Side.ToString(),
			order.Type?.ToString() ?? OrderTypes.Limit.ToString(),
			order.Volume,
			order.Volume - order.Balance,
			order.Type == OrderTypes.Limit ? order.Price : null,
			order.TimeInForce?.ToString() ?? TimeInForce.PutInQueue.ToString(),
			order.State.ToString(),
			Utc(order.ServerTime),
			order.AveragePrice);

	// Everything the venue reports is already UTC; a moment that says nothing about its zone is taken at
	// its word rather than shifted by whatever zone this machine happens to be in.
	private static DateTime Utc(DateTime time)
		=> time.Kind switch
		{
			DateTimeKind.Utc => time,
			DateTimeKind.Unspecified => DateTime.SpecifyKind(time, DateTimeKind.Utc),
			_ => time.ToUniversalTime(),
		};

	private Connector Open()
	{
		var connector = new Connector();

		try
		{
			connector.Adapter.InnerAdapters.Add(_adapters.Create(connector.TransactionIdGenerator));
		}
		catch
		{
			connector.Dispose();
			throw;
		}

		return connector;
	}

	/// <summary>
	/// Waits until the strategy is really running.
	/// </summary>
	/// <param name="strategy">Strategy that was asked to start.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <remarks>
	/// Asking a strategy to start returns before it has: the state is still stopped for a moment
	/// afterwards. A session handed back in that moment reports that nothing is running, and a caller
	/// reading it would conclude the deployment had died at birth.
	/// </remarks>
	private static async Task Started(Strategy strategy, CancellationToken cancellationToken)
	{
		var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		void OnState(IStrategy _)
		{
			if (strategy.ProcessState == ProcessStates.Started)
				running.TrySetResult();
			else if (strategy.ProcessState == ProcessStates.Stopped && strategy.LastError is { } error)
				running.TrySetException(error);
		}

		strategy.ProcessStateChanged += OnState;

		try
		{
			OnState(strategy);

			await running.Task.WaitAsync(_patience, cancellationToken);
		}
		finally
		{
			strategy.ProcessStateChanged -= OnState;
		}
	}

	private static async Task Connect(Connector connector, CancellationToken cancellationToken)
	{
		var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		void OnConnected() => connected.TrySetResult();
		void OnFailed(Exception error) => connected.TrySetException(error);

		connector.Connected += OnConnected;
		connector.ConnectionError += OnFailed;

		try
		{
			connector.Connect();

			await connected.Task.WaitAsync(_patience, cancellationToken);
		}
		finally
		{
			connector.Connected -= OnConnected;
			connector.ConnectionError -= OnFailed;
		}
	}

	private async Task<Security> Find(Connector connector, string symbol, CancellationToken cancellationToken)
	{
		var found = new TaskCompletionSource<Security>(TaskCreationOptions.RunContinuationsAsynchronously);

		void OnSecurity(Subscription _, Security security)
		{
			if (security.Code.EqualsIgnoreCase(symbol))
				found.TrySetResult(security);
		}

		connector.SecurityReceived += OnSecurity;

		try
		{
			connector.Subscribe(new(new SecurityLookupMessage
			{
				SecurityId = new() { SecurityCode = symbol, BoardCode = _adapters.Boards.Of(symbol) },
			}));

			return await found.Task.WaitAsync(_patience, cancellationToken);
		}
		catch (TimeoutException)
		{
			throw new InvalidOperationException(
				$"The broker did not answer with '{symbol}' within {_patience.TotalMinutes:0.#} minutes, so " +
				"there is nothing to trade. Check the symbol is one this account can hold.");
		}
		finally
		{
			connector.SecurityReceived -= OnSecurity;
		}
	}

	private static async Task<Portfolio> Account(Connector connector, CancellationToken cancellationToken)
	{
		var found = new TaskCompletionSource<Portfolio>(TaskCreationOptions.RunContinuationsAsynchronously);

		void OnPortfolio(Subscription _, Portfolio portfolio) => found.TrySetResult(portfolio);

		connector.PortfolioReceived += OnPortfolio;

		try
		{
			connector.Subscribe(new(DataType.PositionChanges, (Security)null));

			return await found.Task.WaitAsync(_patience, cancellationToken);
		}
		catch (TimeoutException)
		{
			throw new InvalidOperationException(
				"The broker did not report the account within " +
				$"{_patience.TotalMinutes:0.#} minutes, so there is nothing to trade on.");
		}
		finally
		{
			connector.PortfolioReceived -= OnPortfolio;
		}
	}

	private sealed class Session : IPaperSession
	{
		private readonly Connector _connector;
		private readonly Strategy _strategy;
		private readonly string _symbol;
		private readonly List<Fill> _fills = [];
		private readonly HashSet<Order> _orders = [];
		private readonly HashSet<DateTime> _days = [];
		private readonly Lock _sync = new();

		private decimal _realized;
		private int _roundTrips;
		private bool _stopped;

		public Session(Connector connector, Strategy strategy, string symbol, string account)
		{
			_connector = connector;
			_strategy = strategy;
			_symbol = symbol;

			Account = account ?? string.Empty;

			_strategy.OwnTradeReceived += OnTrade;
			_strategy.OrderReceived += OnOrder;
			_strategy.PnLReceived2 += OnPnL;
			_strategy.ProcessStateChanged += OnState;
			_connector.CandleReceived += OnCandle;
		}

		public bool IsRunning => !_stopped && _strategy.ProcessState != ProcessStates.Stopped;

		public int OrdersPlaced
		{
			get
			{
				using (_sync.EnterScope())
					return _orders.Count;
			}
		}

		public int Trades
		{
			get
			{
				using (_sync.EnterScope())
					return _roundTrips;
			}
		}

		public int SessionDays
		{
			get
			{
				using (_sync.EnterScope())
					return _days.Count;
			}
		}

		public decimal RealizedProfit => _realized;

		public decimal Position => _strategy.Position;

		public int WorkingOrders
		{
			get
			{
				using (_sync.EnterScope())
					return _orders.Count(o => o.State is OrderStates.Active or OrderStates.Pending);
			}
		}

		public string Account { get; }

		public string Error { get; private set; }

		public async Task StopAsync(bool closePosition, CancellationToken cancellationToken)
		{
			if (_stopped)
				return;

			_stopped = true;

			// Closing is a trade like any other, so it is placed while the strategy still has a connection
			// and before anything is torn down.
			if (closePosition && _strategy.Position != 0)
				_strategy.ClosePosition();

			// Working orders are cancelled by the platform on the way into Stopping, because
			// CancelOrdersWhenStopping is on by default. Nothing here has to ask for it, and nothing here
			// may turn it off: leaving a position open is a choice the caller made, and leaving orders
			// working at a venue after the strategy has gone is not something anybody asked for.
			await _strategy.StopAsync(cancellationToken);

			_connector.Disconnect();
		}

		public async ValueTask DisposeAsync()
		{
			_strategy.OwnTradeReceived -= OnTrade;
			_strategy.OrderReceived -= OnOrder;
			_strategy.PnLReceived2 -= OnPnL;
			_strategy.ProcessStateChanged -= OnState;
			_connector.CandleReceived -= OnCandle;

			if (!_stopped)
				await StopAsync(closePosition: false, CancellationToken.None);

			_connector.Dispose();
		}

		private void OnCandle(Subscription subscription, ICandleMessage candle)
		{
			if (candle.State != CandleStates.Finished)
				return;

			using (_sync.EnterScope())
				_days.Add(candle.OpenTime.Date);
		}

		private void OnTrade(Subscription _, MyTrade trade)
		{
			var fill = new Fill(
				_symbol,
				trade.Order.Side == Sides.Buy ? TradeDirections.Long : TradeDirections.Short,
				DateTime.SpecifyKind(trade.Trade.ServerTime, DateTimeKind.Utc),
				trade.Trade.Price,
				trade.Trade.Volume,
				trade.Commission ?? 0m,

				// The venue charges what it charges; nothing here is estimated on top of it.
				0m);

			using (_sync.EnterScope())
			{
				// Kept in time order rather than in arrival order, and counted here rather than rebuilt on
				// every read. A venue reports fills as they reach it, which is not always as they happened;
				// one fill behind the one before it used to make every later read of this deployment throw,
				// including the read inside stopping it, and no retry could clear it because the list was
				// never repaired.
				var at = _fills.FindLastIndex(f => f.Time <= fill.Time) + 1;

				_fills.Insert(at, fill);

				_roundTrips = RoundTripBuilder.Build([.. _fills]).Count;
			}
		}

		private void OnOrder(Subscription _, Order order)
		{
			using (_sync.EnterScope())
				_orders.Add(order);
		}

		private void OnPnL(Subscription _, Portfolio __, DateTime ___, decimal realized, decimal? ____, decimal? _____)
			=> _realized = realized;

		private void OnState(IStrategy strategy)
		{
			if (strategy.ProcessState == ProcessStates.Stopped && _strategy.LastError is { } error)
				Error = error.Message;
		}
	}
}
