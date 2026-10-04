namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// What to run against the broker.
/// </summary>
/// <param name="Assembly">The compiled candidate.</param>
/// <param name="ClassName">Name of the strategy inside it.</param>
/// <param name="Parameters">Values the run was measured with.</param>
/// <param name="Symbol">Symbol to trade.</param>
/// <param name="TimeFrame">Length of one candle, which the rules are evaluated on.</param>
/// <param name="Volume">Size of one position.</param>
public sealed record PaperRequest(
	byte[] Assembly,
	string ClassName,
	IReadOnlyDictionary<string, decimal> Parameters,
	string Symbol,
	TimeSpan TimeFrame,
	decimal Volume);

/// <summary>
/// A strategy running against a broker, seen from outside.
/// </summary>
/// <remarks>
/// Everything here is what the broker reported, not what the strategy believes. The two differ, and the
/// difference is the entire reason for running on paper at all.
/// </remarks>
public interface IPaperSession : IAsyncDisposable
{
	/// <summary>Whether it is still trading.</summary>
	bool IsRunning { get; }

	/// <summary>Orders the strategy sent.</summary>
	int OrdersPlaced { get; }

	/// <summary>Positions it has opened and closed.</summary>
	int Trades { get; }

	/// <summary>Distinct trading days the strategy has received candles for.</summary>
	int SessionDays { get; }

	/// <summary>What those came to, as the broker reports it.</summary>
	decimal RealizedProfit { get; }

	/// <summary>What it is holding now. Anything other than zero is money at risk.</summary>
	decimal Position { get; }

	/// <summary>Orders it still has live at the venue, which is a different question from the position.</summary>
	/// <remarks>
	/// A stop leaves a position open only when the caller asked it to; it never leaves orders working,
	/// because nobody asked for those. This is what makes the difference visible either way.
	/// </remarks>
	int WorkingOrders { get; }

	/// <summary>
	/// What the broker calls the account this is trading on.
	/// </summary>
	/// <remarks>
	/// Reported rather than assumed, so that a state report names the account somebody's money is in
	/// rather than the connector that reached it.
	/// </remarks>
	string Account { get; }

	/// <summary>Why it stopped by itself, when it did.</summary>
	string Error { get; }

	/// <summary>
	/// Stops the strategy.
	/// </summary>
	/// <param name="closePosition">
	/// Whether to close what it is holding. There is no safe default: leaving a position open keeps money
	/// at risk with nothing watching it, and closing one trades on the caller's behalf. The caller says
	/// which of the two it means.
	/// </param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task StopAsync(bool closePosition, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a compiled candidate against a live paper account.
/// </summary>
/// <remarks>
/// The broker sits behind this so the rest of the product does not have to know which one it is, and so
/// that a deployment can be exercised in a test without an account.
/// </remarks>
public interface IPaperTrader
{
	/// <summary>Name of the account this trades against, recorded so a result says where it ran.</summary>
	string Name { get; }

	/// <summary>
	/// Starts the strategy.
	/// </summary>
	/// <param name="request">What to run.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The running strategy.</returns>
	Task<IPaperSession> StartAsync(PaperRequest request, CancellationToken cancellationToken);
}
