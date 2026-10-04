namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Odysseus.Domain;

/// <summary>
/// What a run is charged for trading, per unit traded, on entry and again on exit.
/// </summary>
/// <param name="Fees">
/// What the broker and the regulator take. On an American equity account the commission itself is
/// usually nothing and this is the regulatory fees, which are small but not zero.
/// </param>
/// <param name="HalfSpread">
/// What crossing the spread costs, per unit. A market order pays about half the spread, so a stock
/// quoted two cents wide costs about a cent a share to get into and another to get out of.
/// </param>
/// <remarks>
/// Both are charged rather than one being folded into the fill price, because of how the run is
/// executed: over candles, a market order fills at the average price of the bar after the one the
/// decision was made on, and no order book is synthesized for it to cross. Nothing in that path
/// charges for the spread, so it is charged here — as a number that is stated and can be varied,
/// rather than as an assumption buried in a fill.
///
/// Neither is zero by default. A candidate that only works without costs is not a candidate, and the
/// cheapest way to discover that is to charge it from the first run rather than the last.
/// </remarks>
public sealed record ExecutionCosts(decimal Fees, decimal HalfSpread)
{
	/// <summary>What one unit costs to trade, both parts together.</summary>
	public decimal PerUnit => Fees + HalfSpread;

	/// <summary>
	/// An American equity account on a liquid stock quoted about two cents wide.
	/// </summary>
	public static ExecutionCosts Default { get; } = new(Fees: 0.0002m, HalfSpread: 0.01m);

	/// <summary>
	/// The same costs multiplied, for the scenario that asks whether the edge survives a worse market.
	/// </summary>
	/// <param name="factor">What to multiply by.</param>
	/// <returns>The multiplied costs.</returns>
	public ExecutionCosts Scaled(decimal factor)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);

		return new(Fees * factor, HalfSpread * factor);
	}
}

/// <summary>
/// The bars a run is measured over, and where they are.
/// </summary>
/// <param name="Folder">Folder holding them, in the trading engine's own market-data storage.</param>
/// <param name="From">Open time of the first bar, in UTC.</param>
/// <param name="To">End of the last bar, exclusive, in UTC.</param>
/// <param name="Count">How many bars lie in that range.</param>
/// <remarks>
/// A range and not the bars themselves. The bars were put into the engine's shape when they were
/// imported and have stayed in it since, so a run opens the folder they are already in rather than
/// being handed a copy that has to be turned back into candles before anything can be measured.
///
/// The folder is the shared storage and holds every slice; the run reads only from <c>From</c> to
/// <c>To</c>. The candidate itself cannot open files, because its source is refused at build time if it
/// reaches the file system.
/// </remarks>
public sealed record BarRange(string Folder, DateTime From, DateTime To, int Count);

/// <summary>
/// One backtest to run.
/// </summary>
/// <param name="Assembly">The compiled candidate.</param>
/// <param name="ClassName">Full name of the strategy type inside it.</param>
/// <param name="Parameters">Values to set on the strategy before it starts.</param>
/// <param name="Symbol">Symbol to trade.</param>
/// <param name="TimeFrame">Length of one candle.</param>
/// <param name="Bars">The bars of the slice.</param>
/// <param name="StartingEquity">Money the account starts with.</param>
/// <param name="Volume">Size of one position.</param>
/// <param name="PriceStep">Smallest price movement of the instrument.</param>
/// <param name="Costs">What the run is charged.</param>
/// <param name="EntryDelayBars">Candles between an entry signal and the order it produces.</param>
public sealed record BacktestRequest(
	byte[] Assembly,
	string ClassName,
	IReadOnlyDictionary<string, decimal> Parameters,
	string Symbol,
	TimeSpan TimeFrame,
	BarRange Bars,
	decimal StartingEquity,
	decimal Volume,
	decimal PriceStep,
	ExecutionCosts Costs,
	int EntryDelayBars = 0);

/// <summary>
/// What a backtest did, before anything is made of it.
/// </summary>
/// <param name="Trades">Positions that were opened and closed.</param>
/// <param name="Equity">Account value sampled through the run.</param>
/// <param name="BarsProcessed">Candles the strategy saw.</param>
/// <param name="ExecutionErrorCount">Orders the run could not place or fill.</param>
/// <param name="OrdersPlaced">
/// Orders the strategy sent. Counted apart from the trades, because an order larger than the bar it
/// lands on does not fill at all: without this, a run whose orders were too big to fill is
/// indistinguishable from one whose rules never triggered.
/// </param>
public sealed record BacktestOutcome(
	IReadOnlyList<ExecutedTrade> Trades,
	IReadOnlyList<EquityPoint> Equity,
	int BarsProcessed,
	int ExecutionErrorCount,
	int OrdersPlaced);

/// <summary>
/// Runs a compiled candidate over a slice of history.
/// </summary>
/// <remarks>
/// The trading engine sits behind this so that the use cases above it never name it, and so that a run
/// can be replaced by a fixture in a test without an emulator. What comes back is what happened —
/// trades and an equity curve — and not what to make of it; the measuring is a separate step, and
/// keeping them apart is what lets the same measurement be applied to a paper account later.
/// </remarks>
public interface IBacktestRunner
{
	/// <summary>
	/// Runs one backtest.
	/// </summary>
	/// <param name="request">What to run.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>What happened.</returns>
	Task<BacktestOutcome> RunAsync(BacktestRequest request, CancellationToken cancellationToken);
}
