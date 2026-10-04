namespace Odysseus.Application;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// One setting of the numbers a specification declared, and how it did.
/// </summary>
/// <param name="Parameters">The values that were tried.</param>
/// <param name="Fitness">What the search scored it, on its own scale.</param>
/// <param name="NetProfit">Result after costs, in the account currency.</param>
/// <param name="MaxDrawdownPercent">Deepest peak-to-trough decline, in percent.</param>
/// <param name="Trades">Closed trades.</param>
public sealed record OptimizationTrial(
	IReadOnlyDictionary<string, decimal> Parameters,
	decimal Fitness,
	decimal NetProfit,
	decimal MaxDrawdownPercent,
	int Trades);

/// <summary>
/// A search over the numbers a specification declared.
/// </summary>
/// <param name="Assembly">The compiled candidate.</param>
/// <param name="ClassName">Full name of the strategy type inside it.</param>
/// <param name="Symbol">Symbol to trade.</param>
/// <param name="TimeFrame">Length of one candle.</param>
/// <param name="Bars">The bars of the slice.</param>
/// <param name="StartingEquity">Money the account starts with.</param>
/// <param name="Volume">Size of one position.</param>
/// <param name="PriceStep">Smallest price movement of the instrument.</param>
/// <param name="Costs">What each run is charged.</param>
/// <param name="Population">How many settings live in one generation.</param>
/// <param name="Generations">How many generations to run before stopping.</param>
/// <param name="Seed">
/// What the search's randomness starts from. A genetic search that is not seeded lands somewhere
/// different every time it is asked, and a result nobody can arrive at twice is not a result.
/// </param>
public sealed record OptimizationRequest(
	byte[] Assembly,
	string ClassName,
	string Symbol,
	TimeSpan TimeFrame,
	BarRange Bars,
	decimal StartingEquity,
	decimal Volume,
	decimal PriceStep,
	ExecutionCosts Costs,
	int Population,
	int Generations,
	int Seed);

/// <summary>
/// A walk-forward run: the declared numbers are searched on one stretch of a slice and the best of them
/// tested on the stretch after it, window by window.
/// </summary>
/// <param name="Assembly">The compiled candidate.</param>
/// <param name="ClassName">Full name of the strategy type inside it.</param>
/// <param name="Symbol">Symbol to trade.</param>
/// <param name="TimeFrame">Length of one bar.</param>
/// <param name="Bars">The slice the windows are cut from.</param>
/// <param name="StartingEquity">Money each run starts with.</param>
/// <param name="Volume">Volume per position.</param>
/// <param name="PriceStep">Smallest price movement of the instrument.</param>
/// <param name="Costs">What every run is charged.</param>
/// <param name="InSample">Length of the stretch each window is fitted on.</param>
/// <param name="OutOfSample">Length of the stretch each window is tested on, and the step between windows.</param>
public sealed record WalkForwardRequest(
	byte[] Assembly,
	string ClassName,
	string Symbol,
	TimeSpan TimeFrame,
	BarRange Bars,
	decimal StartingEquity,
	decimal Volume,
	decimal PriceStep,
	ExecutionCosts Costs,
	TimeSpan InSample,
	TimeSpan OutOfSample);

/// <summary>
/// What one walk-forward window came to.
/// </summary>
/// <param name="InSampleFrom">Start of the stretch the numbers were fitted on, in UTC.</param>
/// <param name="InSampleTo">End of the stretch the numbers were fitted on, in UTC.</param>
/// <param name="OutOfSampleFrom">Start of the stretch they were tested on, in UTC.</param>
/// <param name="OutOfSampleTo">End of the stretch they were tested on, in UTC.</param>
/// <param name="Parameters">The numbers the in-sample search chose.</param>
/// <param name="InSampleFitness">What they scored on the stretch they were chosen on.</param>
/// <param name="OutOfSample">What they did on the stretch after it.</param>
public sealed record WalkForwardWindowResult(
	DateTime InSampleFrom,
	DateTime InSampleTo,
	DateTime OutOfSampleFrom,
	DateTime OutOfSampleTo,
	IReadOnlyDictionary<string, decimal> Parameters,
	decimal InSampleFitness,
	BacktestOutcome OutOfSample);

/// <summary>
/// Searches the numbers a specification declared, over one slice of the history.
/// </summary>
/// <remarks>
/// The search ranks; it does not decide. What comes back is every setting it tried with what that
/// setting did, and the caller takes the best one through the ordinary run, measured the ordinary way.
/// A search that reported its own winner as a result would be marking its own work.
///
/// The ranges come from the specification, not from the caller: each number was declared with its
/// bounds and its step, and those are what the strategy carries into the search.
/// </remarks>
public interface IStrategyOptimizer
{
	/// <summary>
	/// Runs the search.
	/// </summary>
	/// <param name="request">What to search.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Every setting that was evaluated, best first.</returns>
	Task<IReadOnlyList<OptimizationTrial>> SearchAsync(OptimizationRequest request, CancellationToken cancellationToken);

	/// <summary>
	/// Runs a walk-forward over the slice.
	/// </summary>
	/// <param name="request">What to run.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>One result per window, oldest first.</returns>
	Task<IReadOnlyList<WalkForwardWindowResult>> WalkForwardAsync(WalkForwardRequest request, CancellationToken cancellationToken);
}
